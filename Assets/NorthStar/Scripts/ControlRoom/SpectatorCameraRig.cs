// Assignment 3 / Task 1 - Every camera view from the Assignment 2 taxonomy, rendered simultaneously.
//
// Each view is a non-XR Unity camera that renders into its own RenderTexture ("monitor") so a dashboard
// (Task 2) can show them side by side. One extra "program" camera copies whichever view is selected and
// renders it to the desktop Game view / display 0 - that is the feed OBS captures for the audience.
//
// Transitions from the taxonomy (hard cut, smooth/spline-like blend, occlusion recovery) are implemented
// as program-switching behaviour rather than as separate cameras.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NorthStar.ControlRoom
{
    public enum ViewFamily
    {
        RawFpv,
        SmoothedFpv,
        CroppedFpv,
        HorizonFpv,
        OverShoulder,
        CloseOrbit,
        TrailingFollow,
        TorsoAnchored,
        WideEstablishing,
        IsometricTactical,
        DynamicTracking,
        CraneDolly,
        CloseInteraction,
        Legacy, // cameras placed by hand in a scene (e.g. Assignment 2 Left/Right director cameras)
    }

    public enum TransitionKind { HardCut, SmoothBlend }

    [Serializable]
    public class SpectatorViewConfig
    {
        public string Id;
        public string Label;
        public ViewFamily Family;
        public float FieldOfView = 60;
        [Tooltip("Meaning depends on family: shoulder offset, boat-relative position, orbit radius in x, ...")]
        public Vector3 Offset;
        [Tooltip("Exponential smoothing rate (1/s). 0 = no smoothing.")]
        public float Smoothing;

        public SpectatorViewConfig(string id, string label, ViewFamily family, float fov, Vector3 offset, float smoothing)
        {
            Id = id; Label = label; Family = family; FieldOfView = fov; Offset = offset; Smoothing = smoothing;
        }
    }

    public class SpectatorView
    {
        public SpectatorViewConfig Config;
        public Camera Camera;
        public RenderTexture Monitor;
        public bool Adopted;

        // Per-view spatial telemetry (line of sight / occlusion / framing), logged by the recorder.
        public bool SubjectVisible;
        public float SubjectDistance;
        public float OccludedSeconds;
        public bool SubjectInFrame;

        internal bool HasPose;
        internal Vector3 Position;
        internal Quaternion Rotation = Quaternion.identity;
        internal float Phase;
        internal float NextRenderTime;

        public string Id => Config.Id;
    }

    [DefaultExecutionOrder(1000)] // after OVRCameraRig and gameplay have moved things this frame
    public class SpectatorCameraRig : MonoBehaviour
    {
        public static SpectatorCameraRig Instance { get; private set; }

        [Header("Monitors")]
        [SerializeField] private Vector2Int m_monitorResolution = new(640, 360);
        [Tooltip("How often each monitor re-renders. The program output always renders every frame.")]
        [SerializeField] private float m_monitorFps = 15;
        [SerializeField] private bool m_adoptSceneDirectorCameras = true;

        [Header("Program output")]
        [SerializeField] private string m_initialProgramView = "wide_establishing";
        [SerializeField] private TransitionKind m_defaultTransition = TransitionKind.HardCut;
        [SerializeField] private float m_blendDuration = 0.75f;
        [SerializeField] private float m_programDepth = 200;

        [Header("Occlusion")]
        [Tooltip("Layers that never count as blocking the subject (player body, triggers, water, ropes).")]
        [SerializeField] private string[] m_ignoredOcclusionLayers =
            { "Player", "PlayerTriggers", "Ignore Raycast", "TeleportLayer", "HiddenMesh", "NarrativeTrigggers", "Water", "Rope", "Character" };

        [SerializeField] private List<SpectatorViewConfig> m_viewConfigs = DefaultViews();

        private readonly List<SpectatorView> m_views = new();
        private readonly Dictionary<string, SpectatorView> m_byId = new();
        private Camera m_programCamera;
        private int m_occlusionMask;
        private float m_orbitAngle;
        private float m_nextAdoptScan;
        private Vector3 m_programPosition;
        private Quaternion m_programRotation = Quaternion.identity;

        private SpectatorView m_blendFrom;
        private float m_blendStart;
        private float m_blendLength;

        public IReadOnlyList<SpectatorView> Views => m_views;
        public SpectatorView ProgramView { get; private set; }
        public string ProgramViewId => ProgramView?.Id ?? "";
        public Camera ProgramCamera => m_programCamera;
        public bool IsBlending => m_blendFrom != null;

        /// <summary>(previous id, new id, transition)</summary>
        public event Action<string, string, TransitionKind> ProgramChanged;

        public static List<SpectatorViewConfig> DefaultViews() => new()
        {
            // First-person family
            new("fpv_raw", "Raw HMD / FPV", ViewFamily.RawFpv, 90, Vector3.zero, 0),
            new("fpv_smoothed", "Smoothed FPV", ViewFamily.SmoothedFpv, 85, Vector3.zero, 5),
            new("fpv_cropped", "Cropped-FOV FPV", ViewFamily.CroppedFpv, 50, Vector3.zero, 8),
            new("fpv_horizon", "Horizon-stabilised FPV", ViewFamily.HorizonFpv, 80, Vector3.zero, 6),
            // Third-person / player-relative
            new("over_shoulder", "Over-the-Shoulder", ViewFamily.OverShoulder, 60, new Vector3(0.35f, 0.2f, -1.0f), 4),
            new("close_orbit", "Close Orbit / Chase", ViewFamily.CloseOrbit, 60, new Vector3(1.8f, 0.3f, 12f), 3),
            new("torso_anchored", "Torso-Anchored", ViewFamily.TorsoAnchored, 80, new Vector3(0, -0.45f, 0.12f), 2),
            new("close_interaction", "Close Interaction", ViewFamily.CloseInteraction, 45, new Vector3(0.55f, 0.35f, 0.65f), 4),
            // Boat / world-relative
            new("trailing_follow", "Trailing / Follow", ViewFamily.TrailingFollow, 60, new Vector3(0, 9, -38), 2),
            new("wide_establishing", "Fixed Wide / Establishing", ViewFamily.WideEstablishing, 55, new Vector3(-14, 9, -16), 0),
            // Perspective, kept within ~45 m: NorthStar's ocean is only drawn within a fixed radius of each camera,
            // so an orthographic or far-away "true" isometric camera shows the boat over an empty background.
            new("isometric_tactical", "Isometric / Tactical", ViewFamily.IsometricTactical, 40, new Vector3(45, 45, 45), 2),
            new("dynamic_tracking", "Dynamic Pan / Tracking", ViewFamily.DynamicTracking, 50, new Vector3(3, 3.5f, 7), 3),
            new("crane_dolly", "Crane / Dolly", ViewFamily.CraneDolly, 55, new Vector3(9, 2, 20), 3),
        };

        private void Awake()
        {
            Instance = this;
            ControlRoomContext.UpdateFrame(); // so new cameras can copy the player camera's culling mask
            m_occlusionMask = ~LayerMask.GetMask(m_ignoredOcclusionLayers);

            foreach (var config in m_viewConfigs)
            {
                AddView(config, CreateViewCamera(config.Id), adopted: false);
            }

            m_programCamera = CreateCamera("Program Output", null);
            m_programCamera.depth = m_programDepth;
            m_programCamera.targetDisplay = 0;

            SelectView(m_initialProgramView, TransitionKind.HardCut);
        }

        private void OnEnable() => RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
        private void OnDisable() => RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;

        private void OnDestroy()
        {
            foreach (var view in m_views)
            {
                if (view.Monitor != null) view.Monitor.Release();
            }
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- public API (used by dashboard / director)

        public bool TryGetView(string id, out SpectatorView view) => m_byId.TryGetValue(id ?? "", out view);

        public bool SelectView(string id) => SelectView(id, m_defaultTransition);

        public bool SelectView(string id, TransitionKind transition)
        {
            if (!TryGetView(id, out var next)) return false;
            if (next == ProgramView) return true;

            var previous = ProgramView;
            if (transition == TransitionKind.SmoothBlend && previous != null && previous.HasPose)
            {
                m_blendFrom = previous;
                m_blendStart = Time.unscaledTime;
                m_blendLength = Mathf.Max(0.01f, m_blendDuration);
            }
            else
            {
                m_blendFrom = null;
            }

            ProgramView = next;
            ProgramChanged?.Invoke(previous?.Id ?? "", next.Id, transition);
            return true;
        }

        /// <summary>Saves the current image of one view (or every view when id is null) as PNG. Returns written paths.</summary>
        public List<string> SaveSnapshot(string directory, string id = null)
        {
            var written = new List<string>();
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("HHmmss_fff");
            foreach (var view in m_views)
            {
                if (id != null && view.Id != id) continue;
                if (view.Monitor == null) continue;
                var path = Path.Combine(directory, $"{stamp}_{view.Id}.png");
                File.WriteAllBytes(path, ReadMonitor(view.Monitor).EncodeToPNG());
                written.Add(path);
            }
            return written;
        }

        public static Texture2D ReadMonitor(RenderTexture rt, Texture2D reuse = null)
        {
            var texture = reuse != null && reuse.width == rt.width && reuse.height == rt.height
                ? reuse
                : new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            return texture;
        }

        // ---------------------------------------------------------------- per-frame update

        private void LateUpdate()
        {
            ControlRoomContext.UpdateFrame();
            if (m_adoptSceneDirectorCameras && Time.unscaledTime >= m_nextAdoptScan)
            {
                m_nextAdoptScan = Time.unscaledTime + 2f;
                AdoptSceneCameras();
            }

            var dt = Time.deltaTime;
            m_orbitAngle += dt;

            for (var i = 0; i < m_views.Count; i++)
            {
                var view = m_views[i];
                if (view.Camera == null) continue;

                if (!view.Adopted)
                {
                    ComputePose(view, dt, out var position, out var rotation);
                    ApplySmoothing(view, position, rotation, dt);
                    view.Camera.transform.SetPositionAndRotation(view.Position, view.Rotation);
                }
                else
                {
                    view.Position = view.Camera.transform.position;
                    view.Rotation = view.Camera.transform.rotation;
                    view.HasPose = true;
                }

                UpdateViewTelemetry(view, dt);

                // Staggered monitor rendering keeps 13+ extra cameras affordable.
                var due = Time.unscaledTime >= view.NextRenderTime;
                view.Camera.enabled = due;
                if (due) view.NextRenderTime = Time.unscaledTime + 1f / Mathf.Max(1f, m_monitorFps) + view.Phase * 0.001f;
            }

            UpdateProgramCamera();
        }

        private void UpdateProgramCamera()
        {
            if (ProgramView == null || m_programCamera == null) return;

            var target = ProgramView.Camera;
            var position = ProgramView.Position;
            var rotation = ProgramView.Rotation;
            var fov = target.fieldOfView;

            if (m_blendFrom != null)
            {
                var t = Mathf.Clamp01((Time.unscaledTime - m_blendStart) / m_blendLength);
                var eased = t * t * (3 - 2 * t);
                position = Vector3.Lerp(m_blendFrom.Position, position, eased);
                rotation = Quaternion.Slerp(m_blendFrom.Rotation, rotation, eased);
                fov = Mathf.Lerp(m_blendFrom.Camera.fieldOfView, fov, eased);
                if (t >= 1f) m_blendFrom = null;
            }

            m_programPosition = position;
            m_programRotation = rotation;
            m_programCamera.transform.SetPositionAndRotation(position, rotation);
            m_programCamera.orthographic = target.orthographic && m_blendFrom == null;
            m_programCamera.orthographicSize = target.orthographicSize;
            m_programCamera.fieldOfView = fov;
            m_programCamera.cullingMask = target.cullingMask;
        }

        // ---------------------------------------------------------------- NorthStar fake boat movement

        /// <summary>
        /// NorthStar keeps the boat (and the player on it) at a fixed spot for physics and gameplay, and only moves it to
        /// its real world position for rendering: FakeMovement.LateUpdate (execution order 8000) moves it, and it moves back
        /// at end of frame. Poses and occlusion raycasts above are computed in the physics frame, where the colliders are,
        /// so just before cameras render we apply the same offset. Otherwise the cameras would render the empty spot the
        /// boat left behind. Cameras placed under the boat in the scene (legacy views) move with it automatically.
        /// </summary>
        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            var fake = ControlRoomContext.Boat != null ? ControlRoomContext.Boat.MovementSource : null;
            if (fake == null) return;

            // Map from where the boat is for physics to where it actually is right now (identity when it has not moved).
            var delta = fake.transform.rotation * Quaternion.Inverse(fake.OriginalRotation);
            var from = fake.OriginalPosition;
            var to = fake.transform.position;

            foreach (var view in m_views)
            {
                if (view.Adopted || view.Camera == null || !view.HasPose) continue;
                view.Camera.transform.SetPositionAndRotation(delta * (view.Position - from) + to, delta * view.Rotation);
            }

            if (m_programCamera != null && ProgramView != null)
                m_programCamera.transform.SetPositionAndRotation(delta * (m_programPosition - from) + to, delta * m_programRotation);
        }

        private void UpdateViewTelemetry(SpectatorView view, float dt)
        {
            var subject = ControlRoomContext.SubjectPosition;
            var from = view.Position;
            view.SubjectDistance = Vector3.Distance(from, subject);

            var viewport = view.Camera.WorldToViewportPoint(subject);
            view.SubjectInFrame = viewport.z > 0 && viewport.x is > 0 and < 1 && viewport.y is > 0 and < 1;

            // First-person views sit inside the head, so the subject is trivially "visible".
            var isFpv = view.Config.Family is ViewFamily.RawFpv or ViewFamily.SmoothedFpv or ViewFamily.CroppedFpv or ViewFamily.HorizonFpv;
            view.SubjectVisible = isFpv || !Physics.Linecast(from, subject, m_occlusionMask, QueryTriggerInteraction.Ignore);
            view.OccludedSeconds = view.SubjectVisible ? 0 : view.OccludedSeconds + dt;
        }

        // ---------------------------------------------------------------- view poses

        private void ComputePose(SpectatorView view, float dt, out Vector3 position, out Quaternion rotation)
        {
            var config = view.Config;
            var head = ControlRoomContext.Head;
            var headPos = ControlRoomContext.SubjectPosition;
            var headRot = head != null ? head.rotation : Quaternion.identity;
            var headYaw = ControlRoomContext.YawOnly(headRot);

            var boat = ControlRoomContext.Boat;
            var anchor = boat != null ? boat.transform.position : headPos;
            var anchorYaw = boat != null ? ControlRoomContext.BoatYaw : headYaw;

            switch (config.Family)
            {
                case ViewFamily.RawFpv:
                case ViewFamily.SmoothedFpv:
                case ViewFamily.CroppedFpv:
                    position = headPos;
                    rotation = headRot;
                    break;

                case ViewFamily.HorizonFpv:
                    position = headPos;
                    rotation = Quaternion.LookRotation(headRot * Vector3.forward, Vector3.up); // removes roll
                    break;

                case ViewFamily.OverShoulder:
                    position = PullInFromGeometry(headPos, headPos + headYaw * config.Offset);
                    rotation = Quaternion.LookRotation(headPos + headYaw * Vector3.forward * 4f + Vector3.down * 0.3f - position, Vector3.up);
                    break;

                case ViewFamily.CloseOrbit:
                {
                    var angle = m_orbitAngle * config.Offset.z; // offset.z = degrees per second
                    var around = Quaternion.Euler(0, angle, 0) * Vector3.forward * config.Offset.x;
                    position = PullInFromGeometry(headPos, headPos + around + Vector3.up * config.Offset.y);
                    rotation = Quaternion.LookRotation(headPos + Vector3.down * 0.1f - position, Vector3.up);
                    break;
                }

                case ViewFamily.TorsoAnchored:
                {
                    var torso = headPos + Vector3.up * config.Offset.y;
                    position = torso + headYaw * new Vector3(0, 0, config.Offset.z);
                    rotation = headYaw * Quaternion.Euler(25, 0, 0); // chest-mounted, looking at the hands
                    break;
                }

                case ViewFamily.CloseInteraction:
                {
                    var focus = ControlRoomContext.InteractionFocus;
                    position = PullInFromGeometry(focus, focus + headYaw * config.Offset);
                    rotation = Quaternion.LookRotation(focus - position, Vector3.up);
                    break;
                }

                case ViewFamily.TrailingFollow:
                    position = anchor + anchorYaw * config.Offset;
                    rotation = Quaternion.LookRotation(anchor + anchorYaw * new Vector3(0, 2, 12) - position, Vector3.up);
                    break;

                case ViewFamily.WideEstablishing:
                    position = anchor + anchorYaw * config.Offset;
                    rotation = Quaternion.LookRotation(anchor + Vector3.up * 2f - position, Vector3.up);
                    break;

                case ViewFamily.IsometricTactical:
                {
                    // offset = (pitch, yaw relative to boat, distance)
                    var iso = anchorYaw * Quaternion.Euler(config.Offset.x, config.Offset.y, 0);
                    position = anchor - iso * Vector3.forward * config.Offset.z;
                    rotation = iso;
                    break;
                }

                case ViewFamily.DynamicTracking:
                    position = anchor + anchorYaw * config.Offset;
                    rotation = Quaternion.LookRotation(headPos - position, Vector3.up);
                    break;

                case ViewFamily.CraneDolly:
                {
                    // offset = (radius, low height, cycle seconds): sweep from a low side angle up to a high rear angle and back.
                    var t = Mathf.SmoothStep(0, 1, Mathf.PingPong(Time.time / Mathf.Max(1f, config.Offset.z), 1));
                    var low = new Vector3(config.Offset.x, config.Offset.y, 0);
                    var high = new Vector3(0, config.Offset.x * 1.3f, -config.Offset.x * 1.3f);
                    position = anchor + anchorYaw * Vector3.Slerp(low, high, t);
                    rotation = Quaternion.LookRotation(headPos - position, Vector3.up);
                    break;
                }

                default:
                    position = view.Camera.transform.position;
                    rotation = view.Camera.transform.rotation;
                    break;
            }
        }

        private static void ApplySmoothing(SpectatorView view, Vector3 position, Quaternion rotation, float dt)
        {
            var rate = view.Config.Smoothing;
            if (!view.HasPose || rate <= 0 || dt <= 0)
            {
                view.Position = position;
                view.Rotation = rotation;
                view.HasPose = true;
                return;
            }

            var k = 1f - Mathf.Exp(-rate * dt);
            // FPV variants keep position locked to the head; only rotation is damped.
            var lockPosition = view.Config.Family is ViewFamily.SmoothedFpv or ViewFamily.CroppedFpv or ViewFamily.HorizonFpv;
            view.Position = lockPosition ? position : Vector3.Lerp(view.Position, position, k);
            view.Rotation = Quaternion.Slerp(view.Rotation, rotation, k);
        }

        /// <summary>Occlusion-recovery for player-relative cameras: keep the camera on the near side of walls/sails.</summary>
        private Vector3 PullInFromGeometry(Vector3 subject, Vector3 desired)
        {
            var direction = desired - subject;
            var distance = direction.magnitude;
            if (distance < 1e-3f) return desired;
            return Physics.SphereCast(subject, 0.1f, direction / distance, out var hit, distance, m_occlusionMask, QueryTriggerInteraction.Ignore)
                ? subject + direction / distance * Mathf.Max(0.2f, hit.distance - 0.05f)
                : desired;
        }

        // ---------------------------------------------------------------- camera creation

        private void AddView(SpectatorViewConfig config, Camera camera, bool adopted)
        {
            var view = new SpectatorView
            {
                Config = config,
                Camera = camera,
                Adopted = adopted,
                Phase = m_views.Count,
                Monitor = new RenderTexture(new RenderTextureDescriptor(m_monitorResolution.x, m_monitorResolution.y, RenderTextureFormat.ARGB32, 24) { sRGB = true })
                {
                    name = "Monitor_" + config.Id,
                },
            };
            view.Monitor.Create();
            camera.targetTexture = view.Monitor;

            if (!adopted)
            {
                camera.fieldOfView = config.FieldOfView;
            }

            // Stagger first renders so all monitors do not land on the same frame.
            view.NextRenderTime = Time.unscaledTime + view.Phase / (Mathf.Max(1f, m_monitorFps) * Mathf.Max(1, m_viewConfigs.Count));

            m_views.Add(view);
            m_byId[config.Id] = view;
        }

        private Camera CreateViewCamera(string id) => CreateCamera("View_" + id, transform);

        private Camera CreateCamera(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : transform, false);
            var camera = go.AddComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 3000f;

            var player = ControlRoomContext.PlayerCamera != null ? ControlRoomContext.PlayerCamera : Camera.main;
            if (player != null)
            {
                camera.cullingMask = player.cullingMask;
                camera.clearFlags = player.clearFlags;
                camera.backgroundColor = player.backgroundColor;
            }

            var data = camera.GetUniversalAdditionalCameraData();
            data.allowXRRendering = false; // URP's way to keep spectator cameras out of the headset (stereoTargetEye is built-in RP only)
            data.renderPostProcessing = player != null && player.GetUniversalAdditionalCameraData().renderPostProcessing;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            return camera;
        }

        private void AdoptSceneCameras()
        {
            // Only looks for the few well-known names used by the Assignment 2 prototype (throttled by the caller).
            foreach (var name in s_legacyCameraNames)
            {
                var id = "legacy_" + name;
                if (m_byId.ContainsKey(id)) continue;
                var go = GameObject.Find(name);
                if (go == null || !go.TryGetComponent<Camera>(out var camera)) continue;

                    camera.GetUniversalAdditionalCameraData().allowXRRendering = false;
                AddView(new SpectatorViewConfig(id, name, ViewFamily.Legacy, camera.fieldOfView, Vector3.zero, 0), camera, adopted: true);
            }

            // Drop adopted cameras whose scene was unloaded.
            for (var i = m_views.Count - 1; i >= 0; i--)
            {
                if (m_views[i].Camera != null) continue;
                var dead = m_views[i];
                if (dead == ProgramView) SelectView(m_initialProgramView, TransitionKind.HardCut);
                dead.Monitor?.Release();
                m_byId.Remove(dead.Id);
                m_views.RemoveAt(i);
            }
        }

        private static readonly string[] s_legacyCameraNames = { "LeftDirectorCamera", "RightDirectorCamera", "PlayerDirectorCamera" };
    }
}
