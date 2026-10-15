// Assignment 3 / Task 1 - Exhaustive variable extraction.
//
// Replaces the Assignment 2 NorthStarTelemetry prototype (1 Hz, steering-triggered, 14 columns) with continuous,
// unfiltered capture. Each play session writes a folder:
//
//   ControlRoomSessions/<yyyyMMdd_HHmmss>_<player>/
//     session.json        metadata: player, start time, device, scenes, column list, view list
//     telemetry.csv       wide, fixed schema, sampled every frame up to SampleRate (kinematic, spatial,
//                         interactive, biometric-proxy, game-state, system)
//     views.csv           per camera view: subject visible / in frame / distance / occluded time / on program
//     events.csv          every UnityEvent on game components + derived events (grabs, tasks, scenes, cuts, ...)
//     state_changes.csv   every field of every game component + every physics body, as snapshots + deltas
//
// The same telemetry rows (and events) are broadcast as JSON over UDP so the Task 2 dashboard can show them live.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Meta.Utilities.Environment;
using Meta.Utilities.Narrative;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NorthStar.ControlRoom
{
    [DefaultExecutionOrder(1100)] // after SpectatorCameraRig so per-view telemetry is from this frame
    public class ExhaustiveTelemetryRecorder : MonoBehaviour
    {
        public static ExhaustiveTelemetryRecorder Instance { get; private set; }

        [Header("Session")]
        [Tooltip("Shown on the dashboard and stored in session.json. Empty = PlayerPrefs 'ControlRoom.PlayerName' or 'Player'.")]
        [SerializeField] private string m_playerName = "";

        [Header("Rates (Hz)")]
        [SerializeField] private float m_sampleRate = 30;
        [SerializeField] private float m_viewSampleRate = 10;
        [SerializeField] private float m_stateSampleRate = 5;
        [SerializeField] private float m_fullSnapshotInterval = 10;
        [SerializeField] private float m_rescanInterval = 5;

        [Header("Live stream (Task 2 dashboard)")]
        [SerializeField] private bool m_udpEnabled = true;
        [SerializeField] private string m_udpHost = "127.0.0.1";
        [SerializeField] private int m_udpPort = 9050;
        [SerializeField] private float m_udpRate = 10;

        [Header("Scope")]
        [Tooltip("Namespaces whose components are captured by the reflective state sampler and event tap.")]
        [SerializeField] private string[] m_namespaces = { "NorthStar", "Meta.Utilities" };

        public string SessionDirectory { get; private set; }
        public string PlayerName { get; private set; }
        // Stopwatch, not Time.realtimeSinceStartup: Unity's timers are unreliable while play mode is shutting down.
        public float SessionTime => (float)m_clock.Elapsed.TotalSeconds;
        public TelemetryRow LatestRow => m_row;
        public long SampleCount => m_telemetry?.Rows ?? 0;

        /// <summary>Raised for every logged event: (t_session, source, name, args).</summary>
        public event Action<float, string, string, string> EventLogged;

        private CsvLog m_telemetry, m_views, m_events, m_state;
        private readonly TelemetryRow m_row = new();
        private ReflectiveStateSampler m_sampler;
        private UnityEventTap m_tap;
        private UdpClient m_udp;

        private readonly System.Diagnostics.Stopwatch m_clock = new();
        private DateTime m_startedLocal;
        private string m_viewsJson = "[]";
        private float m_nextSample, m_nextViewSample, m_nextStateSample, m_nextSnapshot, m_nextRescan, m_nextUdp, m_nextFlush;
        private bool m_headerWritten;

        // Finite-difference state for velocities.
        private readonly MotionTracker m_headMotion = new(), m_leftMotion = new(), m_rightMotion = new(), m_boatMotion = new();
        private readonly JitterTracker m_leftJitter = new(), m_rightJitter = new(), m_headJitter = new();
        private float m_smoothedFps;

        // Derived-event state.
        private readonly HashSet<string> m_lastTasks = new();
        private readonly HashSet<int> m_lastHeld = new();
        private readonly Dictionary<int, bool> m_lastJointGrab = new();
        private readonly Dictionary<int, string> m_lastHarpoonState = new();
        private string m_lastSubtitle = "";
        private bool m_lastBoatStopped;
        private bool m_lastLoading;

        private static readonly FieldInfo s_harpoonState = typeof(Harpoon).GetField("m_state", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_wheelInteractable = typeof(WheelController).GetField("m_interactable", BindingFlags.Instance | BindingFlags.NonPublic);
        private bool m_oceanHeightFailed;

        // ---------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            PlayerName = !string.IsNullOrWhiteSpace(m_playerName) ? m_playerName : PlayerPrefs.GetString("ControlRoom.PlayerName", "Player");
            m_clock.Start();
            m_startedLocal = DateTime.Now;

            var root = Application.isEditor
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ControlRoomSessions")) // outside Assets: no reimports
                : Path.Combine(Application.persistentDataPath, "ControlRoomSessions");
            SessionDirectory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Sanitize(PlayerName));
            Directory.CreateDirectory(SessionDirectory);

            m_views = new CsvLog(Path.Combine(SessionDirectory, "views.csv"),
                "t_session,frame,view_id,family,on_program,subject_visible,subject_in_frame,subject_distance,occluded_s,pos_x,pos_y,pos_z,yaw,pitch,fov");
            m_events = new CsvLog(Path.Combine(SessionDirectory, "events.csv"), "t_session,t_game,frame,scene,source,event,args");
            m_state = new CsvLog(Path.Combine(SessionDirectory, "state_changes.csv"), ReflectiveStateSampler.HEADER);

            m_sampler = new ReflectiveStateSampler(m_namespaces);
            m_tap = new UnityEventTap(m_namespaces);
            m_tap.Fired += (component, name, args) =>
                LogEvent(component != null ? ControlRoomContext.PathOf(component.transform) + ":" + component.GetType().Name : "", name, args);

            if (m_udpEnabled)
            {
                try { m_udp = new UdpClient(); m_udp.Connect(m_udpHost, m_udpPort); }
                catch (Exception e) { Debug.LogWarning("[ControlRoom] UDP stream disabled: " + e.Message); m_udp = null; }
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Debug.Log($"[ControlRoom] Recording session to {SessionDirectory}");
        }

        private void Start()
        {
            if (SpectatorCameraRig.Instance != null)
                SpectatorCameraRig.Instance.ProgramChanged += (from, to, kind) => LogEvent("ControlRoom", "program_cut", $"{from}->{to} ({kind})");
            LogEvent("ControlRoom", "session_start", PlayerName);
            WriteSessionJson();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            LogEvent("ControlRoom", "session_end", "");
            WriteSessionJson();
            m_telemetry?.Dispose();
            m_views?.Dispose();
            m_events?.Dispose();
            m_state?.Dispose();
            m_udp?.Dispose();
            Instance = null;
        }

        private void OnApplicationPause(bool paused)
        {
            // Quest: the app is paused (not destroyed) when the headset is taken off - make sure data is on disk.
            if (paused) FlushAll();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            LogEvent("SceneManager", "scene_loaded", scene.name + " (" + mode + ")");
            m_nextRescan = 0;
            m_nextSnapshot = 0; // a fresh full snapshot makes each scene self-contained
            ControlRoomContext.ForceRescan();
        }

        private void OnSceneUnloaded(Scene scene) => LogEvent("SceneManager", "scene_unloaded", scene.name);

        // ---------------------------------------------------------------- per-frame

        private void LateUpdate()
        {
            ControlRoomContext.UpdateFrame();
            var now = Time.unscaledTime;
            var dt = Time.unscaledDeltaTime;
            if (dt > 0) m_smoothedFps = Mathf.Lerp(m_smoothedFps <= 0 ? 1f / dt : m_smoothedFps, 1f / dt, 0.1f);

            UpdateMotion();

            if (now >= m_nextRescan)
            {
                m_nextRescan = now + m_rescanInterval;
                m_sampler.Rescan();
                m_tap.Rescan();
            }

            DetectDerivedEvents();

            if (now >= m_nextSample)
            {
                m_nextSample = now + 1f / Mathf.Max(1f, m_sampleRate);
                BuildRow();
                if (!m_headerWritten)
                {
                    m_telemetry = new CsvLog(Path.Combine(SessionDirectory, "telemetry.csv"), m_row.HeaderLine());
                    m_headerWritten = true;
                    WriteSessionJson();
                }
                m_telemetry.WriteLine(m_row.CsvLine());

                if (m_udp != null && now >= m_nextUdp)
                {
                    m_nextUdp = now + 1f / Mathf.Max(1f, m_udpRate);
                    Send(m_row.JsonObject("telemetry"));
                }
            }

            if (now >= m_nextViewSample)
            {
                m_nextViewSample = now + 1f / Mathf.Max(1f, m_viewSampleRate);
                WriteViews();
            }

            if (now >= m_nextStateSample)
            {
                m_nextStateSample = now + 1f / Mathf.Max(0.1f, m_stateSampleRate);
                var snapshot = now >= m_nextSnapshot;
                if (snapshot) m_nextSnapshot = now + m_fullSnapshotInterval;
                m_sampler.Sample(m_state, TelemetryFormat.F(SessionTime), Time.frameCount.ToString(TelemetryFormat.Inv), snapshot);
            }

            if (now >= m_nextFlush)
            {
                m_nextFlush = now + 1f;
                FlushAll();
            }
        }

        private void UpdateMotion()
        {
            var dt = Time.deltaTime;
            m_headMotion.Update(ControlRoomContext.Head, dt);
            m_leftMotion.Update(ControlRoomContext.LeftHand, dt);
            m_rightMotion.Update(ControlRoomContext.RightHand, dt);
            m_headJitter.Update(ControlRoomContext.Head, dt);
            m_leftJitter.Update(ControlRoomContext.LeftHand, dt);
            m_rightJitter.Update(ControlRoomContext.RightHand, dt);

            var boat = ControlRoomContext.Boat;
            if (boat != null && boat.MovementSource != null)
                m_boatMotion.Update(boat.MovementSource.CurrentPosition, boat.MovementSource.CurrentRotation, dt);
            else
                m_boatMotion.Reset();
        }

        // ---------------------------------------------------------------- the wide row

        private void BuildRow()
        {
            var r = m_row;
            r.Clear();

            var head = ControlRoomContext.Head;
            var left = ControlRoomContext.LeftHand;
            var right = ControlRoomContext.RightHand;
            var boat = ControlRoomContext.Boat;
            var fake = boat != null ? boat.MovementSource : null;
            var wheel = ControlRoomContext.Wheel;
            var sail = ControlRoomContext.Sail;
            var env = EnvironmentSystem.Instance;
            var flow = ControlRoomContext.GameFlow;
            var rig = SpectatorCameraRig.Instance;

            // --- system / session -------------------------------------------------------------------
            r.Add("t_session", SessionTime);
            r.Add("t_game", Time.time);
            r.Add("frame", Time.frameCount);
            r.Add("dt", Time.unscaledDeltaTime);
            r.Add("fps", m_smoothedFps);
            r.Add("time_scale", Time.timeScale);
            r.Add("player_name", PlayerName);
            r.Add("scene_active", SceneManager.GetActiveScene().name);
            r.Add("scenes_loaded", string.Join("|", LoadedSceneNames()));
            r.Add("is_loading", flow != null && flow.IsLoading);
            r.Add("load_progress", flow != null ? flow.GetLoadProgress() : 0f);
            r.Add("program_view", rig != null ? rig.ProgramViewId : "");
            r.Add("program_blending", rig != null && rig.IsBlending);
            r.Add("hmd_present", OVRManager.isHmdPresent);
            r.Add("user_present", OVRPlugin.userPresent);
            r.Add("display_hz", OVRPlugin.systemDisplayFrequency);
            r.Add("battery", SystemInfo.batteryLevel);
            r.Add("hand_tracking", OVRPlugin.GetHandTrackingEnabled());
            r.Add("active_controller", OVRInput.GetActiveController().ToString());

            // --- kinematic: head, hands, tracking space ---------------------------------------------
            AddPose(r, "head", head, m_headMotion);
            AddPose(r, "lhand", left, m_leftMotion);
            AddPose(r, "rhand", right, m_rightMotion);
            var tracking = ControlRoomContext.TrackingSpace;
            r.Add("rig_pos", tracking != null ? tracking.position : Vector3.zero, tracking != null);
            r.Add("rig_yaw", tracking != null ? tracking.eulerAngles.y : float.NaN);

            // --- kinematic: boat -----------------------------------------------------------------------
            r.Add("boat_present", boat != null);
            r.Add("boat_pos", boat != null ? boat.transform.position : Vector3.zero, boat != null);
            r.Add("boat_euler", boat != null ? boat.transform.eulerAngles : Vector3.zero, boat != null);
            r.Add("boat_world_pos", fake != null ? fake.CurrentPosition : Vector3.zero, fake != null);
            r.Add("boat_world_euler", fake != null ? fake.CurrentRotation.eulerAngles : Vector3.zero, fake != null);
            r.Add("boat_heading", boat != null ? boat.HeadingAngle : float.NaN);
            r.Add("boat_vel", m_boatMotion.Velocity, m_boatMotion.Valid);
            r.Add("boat_speed", m_boatMotion.Valid ? m_boatMotion.Velocity.magnitude : float.NaN);
            r.Add("boat_yaw_rate", m_boatMotion.Valid ? m_boatMotion.YawRate : float.NaN);
            r.Add("boat_stopped", boat != null && boat.Stopped);
            r.Add("sail_speed", sail != null ? SafeSailSpeed(sail) : float.NaN);

            // --- spatial ------------------------------------------------------------------------------
            r.Add("head_in_boat", head != null && boat != null ? BoatController.WorldToBoatSpace(head.position) : Vector3.zero, head != null && boat != null);
            r.Add("head_height", head != null && tracking != null ? head.position.y - tracking.position.y : float.NaN);
            r.Add("wind_vec", env != null ? env.WindVector : Vector3.zero, env != null);
            r.Add("wind_yaw", env != null ? env.WindYaw : float.NaN);
            r.Add("wind_pitch", env != null ? env.WindPitch : float.NaN);
            r.Add("wind_speed", env != null ? env.WindVector.magnitude : float.NaN);
            r.Add("env_profile", env != null && env.CurrentProfile != null ? env.CurrentProfile.name : "");
            r.Add("ocean_height_at_boat", OceanHeight(env, boat));
            r.Add("dist_wheel", Distance(head, wheel != null ? wheel.transform : null));
            var (harpoon, harpoonDist) = Nearest(head, ControlRoomContext.Harpoons);
            r.Add("dist_harpoon", harpoonDist);
            var (npc, npcDist) = Nearest(head, ControlRoomContext.Npcs);
            r.Add("nearest_npc", npc != null ? npc.name : "");
            r.Add("dist_nearest_npc", npcDist);
            r.Add("npc_count", ControlRoomContext.Npcs.Count(n => n != null));
            r.Add("npc_angle_to_player", NpcAngle(npc, head));

            // --- interactive ----------------------------------------------------------------------------
            r.Add("steering", wheel != null ? wheel.Value : float.NaN);
            r.Add("steering_grabbed", WheelGrabbed(wheel));
            r.Add("steering_override", wheel != null && wheel.UseOverride);
            r.Add("sail_rope_tight", sail != null ? sail.RopeTightPercentage : float.NaN);
            r.Add("sail_wind_starboard", sail != null && sail.WindToStarboard);
            r.Add("sail_dir", sail != null ? SafeSailDirection(sail) : Vector3.zero, sail != null);
            var held = ControlRoomContext.HeldGrabbables;
            r.Add("held_count", held.Count);
            r.Add("held_objects", string.Join("|", held.Where(g => g != null).Select(g => g.name)));
            r.Add("interaction_focus", ControlRoomContext.InteractionFocus, ControlRoomContext.HasInteractionFocus);
            var grabbedJoints = ControlRoomContext.JointInteractables.Where(j => j != null && j.IsGrabbing()).ToList();
            r.Add("joints_grabbed", grabbedJoints.Count);
            r.Add("joints_grabbed_names", string.Join("|", grabbedJoints.Select(j => j.name + "=" + TelemetryFormat.F(j.Value))));
            r.Add("harpoon_state", harpoon != null ? HarpoonState(harpoon) : "");
            AddControllerInput(r, "l", OVRInput.Controller.LTouch, OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Axis1D.PrimaryHandTrigger,
                OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Button.Three, OVRInput.Button.Four, OVRInput.Button.PrimaryThumbstick);
            AddControllerInput(r, "r", OVRInput.Controller.RTouch, OVRInput.Axis1D.SecondaryIndexTrigger, OVRInput.Axis1D.SecondaryHandTrigger,
                OVRInput.Axis2D.SecondaryThumbstick, OVRInput.Button.One, OVRInput.Button.Two, OVRInput.Button.SecondaryThumbstick);

            // --- biometric proxies (Quest exposes no heart-rate/EDA; these are motion-derived) -------
            r.Add("head_jitter", m_headJitter.Rms);
            r.Add("lhand_jitter", m_leftJitter.Rms);
            r.Add("rhand_jitter", m_rightJitter.Rms);
            r.Add("hand_span", left != null && right != null ? Vector3.Distance(left.position, right.position) : float.NaN);
            r.Add("head_pitch", head != null ? SignedAngle(head.eulerAngles.x) : float.NaN);
            r.Add("head_roll", head != null ? SignedAngle(head.eulerAngles.z) : float.NaN);
            r.Add("activity", (m_headMotion.Valid ? m_headMotion.Speed : 0) + (m_leftMotion.Valid ? m_leftMotion.Speed : 0) + (m_rightMotion.Valid ? m_rightMotion.Speed : 0));
            var player = GlobalSettings.PlayerSettings;
            r.Add("calibrated_height", player != null ? player.PlayerHeight : float.NaN);
            r.Add("seated", player != null && player.Seated);
            r.Add("comfort_level", player != null ? player.ComfortLevel.ToString() : "");

            // --- game state / narrative -------------------------------------------------------------------
            var tasks = CurrentTaskIds();
            r.Add("task_count", tasks.Count);
            r.Add("current_tasks", string.Join("|", tasks));
            var (speaker, line) = ActiveSubtitle();
            r.Add("dialogue_speaker", speaker);
            r.Add("dialogue_text", line);
        }

        private void WriteViews()
        {
            var rig = SpectatorCameraRig.Instance;
            if (rig == null) return;
            var t = TelemetryFormat.F(SessionTime);
            var frame = Time.frameCount.ToString(TelemetryFormat.Inv);
            foreach (var view in rig.Views)
            {
                if (view.Camera == null) continue;
                var euler = view.Rotation.eulerAngles;
                m_views.WriteFields(t, frame, view.Id, view.Config.Family.ToString(),
                    TelemetryFormat.B(view == rig.ProgramView),
                    TelemetryFormat.B(view.SubjectVisible),
                    TelemetryFormat.B(view.SubjectInFrame),
                    TelemetryFormat.F(view.SubjectDistance),
                    TelemetryFormat.F(view.OccludedSeconds),
                    TelemetryFormat.F(view.Position.x), TelemetryFormat.F(view.Position.y), TelemetryFormat.F(view.Position.z),
                    TelemetryFormat.F(euler.y), TelemetryFormat.F(SignedAngle(euler.x)),
                    TelemetryFormat.F(view.Camera.orthographic ? 0 : view.Camera.fieldOfView));
            }
        }

        // ---------------------------------------------------------------- derived events

        private void DetectDerivedEvents()
        {
            // Grab start/end on any grabbable.
            var heldNow = new HashSet<int>();
            foreach (var g in ControlRoomContext.HeldGrabbables)
            {
                if (g == null) continue;
                var id = g.GetInstanceID();
                heldNow.Add(id);
                if (!m_lastHeld.Contains(id)) LogEvent(ControlRoomContext.PathOf(g.transform), "grab_start", g.name);
            }
            foreach (var id in m_lastHeld)
            {
                if (!heldNow.Contains(id)) LogEvent("Grabbable#" + id, "grab_end", "");
            }
            m_lastHeld.Clear();
            m_lastHeld.UnionWith(heldNow);

            // Joint interactables (wheel, levers, cranks, sliders, buttons).
            foreach (var joint in ControlRoomContext.JointInteractables)
            {
                if (joint == null) continue;
                var id = joint.GetInstanceID();
                var grabbing = joint.IsGrabbing();
                if (m_lastJointGrab.TryGetValue(id, out var was) && was != grabbing)
                    LogEvent(ControlRoomContext.PathOf(joint.transform), grabbing ? "joint_grab_start" : "joint_grab_end", TelemetryFormat.F(joint.Value));
                m_lastJointGrab[id] = grabbing;
            }

            // Harpoon state machine (Loading -> Locking -> Armed -> Airborne).
            foreach (var harpoon in ControlRoomContext.Harpoons)
            {
                if (harpoon == null) continue;
                var id = harpoon.GetInstanceID();
                var state = HarpoonState(harpoon);
                if (m_lastHarpoonState.TryGetValue(id, out var was) && was != state)
                    LogEvent(ControlRoomContext.PathOf(harpoon.transform), "harpoon_state", was + "->" + state);
                m_lastHarpoonState[id] = state;
            }

            // Narrative tasks.
            var tasks = CurrentTaskIds();
            foreach (var task in tasks)
                if (!m_lastTasks.Contains(task)) LogEvent("TaskManager", "task_started", task);
            foreach (var task in m_lastTasks)
                if (!tasks.Contains(task)) LogEvent("TaskManager", "task_ended", task);
            m_lastTasks.Clear();
            m_lastTasks.UnionWith(tasks);

            // Dialogue lines.
            var (speaker, line) = ActiveSubtitle();
            var key = speaker + ":" + line;
            if (key != m_lastSubtitle)
            {
                if (line.Length > 0) LogEvent("Subtitle", "dialogue_line", key);
                m_lastSubtitle = key;
            }

            var boat = ControlRoomContext.Boat;
            var stopped = boat != null && boat.Stopped;
            if (stopped != m_lastBoatStopped) LogEvent("BoatController", stopped ? "boat_stopped" : "boat_started", "");
            m_lastBoatStopped = stopped;

            var loading = ControlRoomContext.GameFlow != null && ControlRoomContext.GameFlow.IsLoading;
            if (loading != m_lastLoading) LogEvent("GameFlowController", loading ? "loading_started" : "loading_finished", "");
            m_lastLoading = loading;
        }

        /// <summary>Adds a row to events.csv and the live stream. Also usable by the dashboard ("mark" button).</summary>
        public void LogEvent(string source, string name, string args)
        {
            if (m_events == null) return;
            var t = SessionTime;
            m_events.WriteFields(TelemetryFormat.F(t), TelemetryFormat.F(Time.time), Time.frameCount.ToString(TelemetryFormat.Inv),
                SceneManager.GetActiveScene().name, source, name, args);
            if (m_udp != null)
            {
                Send("{\"type\":\"event\",\"t_session\":" + TelemetryFormat.F(t) + ",\"source\":" + TelemetryFormat.Json(source)
                     + ",\"event\":" + TelemetryFormat.Json(name) + ",\"args\":" + TelemetryFormat.Json(args) + "}");
            }
            EventLogged?.Invoke(t, source, name, args);
        }

        // ---------------------------------------------------------------- helpers

        private static void AddPose(TelemetryRow r, string prefix, Transform t, MotionTracker motion)
        {
            var valid = t != null;
            r.Add(prefix + "_pos", valid ? t.position : Vector3.zero, valid);
            r.Add(prefix + "_euler", valid ? t.eulerAngles : Vector3.zero, valid);
            r.Add(prefix + "_rot", valid ? t.rotation : Quaternion.identity, valid);
            r.Add(prefix + "_vel", motion.Velocity, motion.Valid);
            r.Add(prefix + "_speed", motion.Valid ? motion.Speed : float.NaN);
            r.Add(prefix + "_ang_speed", motion.Valid ? motion.AngularSpeed : float.NaN);
        }

        private static void AddControllerInput(TelemetryRow r, string side, OVRInput.Controller controller,
            OVRInput.Axis1D trigger, OVRInput.Axis1D grip, OVRInput.Axis2D stick,
            OVRInput.Button primary, OVRInput.Button secondary, OVRInput.Button stickClick)
        {
            r.Add(side + "_ctrl_connected", OVRInput.IsControllerConnected(controller));
            r.Add(side + "_trigger", OVRInput.Get(trigger));
            r.Add(side + "_grip", OVRInput.Get(grip));
            r.Add(side + "_stick", OVRInput.Get(stick));
            r.Add(side + "_btn_primary", OVRInput.Get(primary));
            r.Add(side + "_btn_secondary", OVRInput.Get(secondary));
            r.Add(side + "_btn_stick", OVRInput.Get(stickClick));
        }

        private static bool WheelGrabbed(WheelController wheel)
        {
            if (wheel == null || s_wheelInteractable == null) return false;
            return s_wheelInteractable.GetValue(wheel) is BaseJointInteractable<float> joint && joint != null && joint.IsGrabbing();
        }

        private static string HarpoonState(Harpoon harpoon) =>
            s_harpoonState != null ? s_harpoonState.GetValue(harpoon)?.ToString() ?? "" : "";

        private static float SafeSailSpeed(SailAngleController sail)
        {
            try { return sail.GetSailSpeed(); }
            catch { return float.NaN; } // needs EnvironmentSystem; absent in some scenes
        }

        private static Vector3 SafeSailDirection(SailAngleController sail)
        {
            try { return sail.GetSailDirection(); }
            catch { return Vector3.zero; }
        }

        private float OceanHeight(EnvironmentSystem env, BoatController boat)
        {
            if (env == null || boat == null || m_oceanHeightFailed) return float.NaN;
            try { return env.GetOceanHeight(boat.transform.position); }
            catch (Exception e)
            {
                m_oceanHeightFailed = true; // log once, then stop trying
                Debug.LogWarning("[ControlRoom] ocean height unavailable: " + e.Message);
                return float.NaN;
            }
        }

        private static float Distance(Transform a, Transform b) =>
            a != null && b != null ? Vector3.Distance(a.position, b.position) : float.NaN;

        private static (T, float) Nearest<T>(Transform from, List<T> candidates) where T : Component
        {
            T best = null;
            var bestDistance = float.NaN;
            if (from == null) return (null, bestDistance);
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                var d = Vector3.Distance(from.position, candidate.transform.position);
                if (best == null || d < bestDistance) { best = candidate; bestDistance = d; }
            }
            return (best, bestDistance);
        }

        private static float NpcAngle(NpcController npc, Transform head)
        {
            if (npc == null || head == null) return float.NaN;
            try { return npc.GetAngleFromTarget(head); }
            catch { return float.NaN; } // NPC rig (hips) not set up yet
        }

        private static float SignedAngle(float degrees) => degrees > 180 ? degrees - 360 : degrees;

        private static List<string> CurrentTaskIds()
        {
            try
            {
                var tasks = TaskManager.CurrentTasks;
                return tasks == null ? new List<string>() : tasks.Select(t => t.ID).ToList();
            }
            catch
            {
                return new List<string>(); // TaskManager not initialised (e.g. Launch scene)
            }
        }

        private static (string speaker, string line) ActiveSubtitle()
        {
            foreach (var subtitle in ControlRoomContext.Subtitles)
            {
                var text = ControlRoomContext.GetSubtitleText(subtitle);
                if (!string.IsNullOrEmpty(text)) return (subtitle.Id ?? "", text);
            }
            return ("", "");
        }

        private static IEnumerable<string> LoadedSceneNames()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++) yield return SceneManager.GetSceneAt(i).name;
        }

        private void Send(string json)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                m_udp.Send(bytes, bytes.Length);
            }
            catch (SocketException)
            {
                // Nobody listening yet (ICMP port unreachable) - expected until the dashboard starts.
            }
        }

        private void FlushAll()
        {
            m_telemetry?.Flush();
            m_views?.Flush();
            m_events?.Flush();
            m_state?.Flush();
        }

        private void WriteSessionJson()
        {
            var rig = SpectatorCameraRig.Instance;
            var sb = new StringBuilder("{\n");
            sb.Append("  \"player_name\": ").Append(TelemetryFormat.Json(PlayerName)).Append(",\n");
            sb.Append("  \"started_local\": ").Append(TelemetryFormat.Json(m_startedLocal.ToString("o"))).Append(",\n");
            sb.Append("  \"duration_s\": ").Append(TelemetryFormat.F(SessionTime)).Append(",\n");
            sb.Append("  \"unity\": ").Append(TelemetryFormat.Json(Application.unityVersion)).Append(",\n");
            sb.Append("  \"device\": ").Append(TelemetryFormat.Json(SystemInfo.deviceModel)).Append(",\n");
            sb.Append("  \"headset\": ").Append(TelemetryFormat.Json(OVRPlugin.GetSystemHeadsetType().ToString())).Append(",\n");
            sb.Append("  \"editor\": ").Append(Application.isEditor ? "true" : "false").Append(",\n");
            sb.Append("  \"sample_rate_hz\": ").Append(TelemetryFormat.F(m_sampleRate)).Append(",\n");
            sb.Append("  \"udp\": ").Append(m_udp != null ? TelemetryFormat.Json(m_udpHost + ":" + m_udpPort) : "null").Append(",\n");
            sb.Append("  \"telemetry_rows\": ").Append(SampleCount).Append(",\n");
            sb.Append("  \"event_rows\": ").Append(m_events?.Rows ?? 0).Append(",\n");
            sb.Append("  \"state_rows\": ").Append(m_state?.Rows ?? 0).Append(",\n");
            sb.Append("  \"state_components\": ").Append(m_sampler?.TrackedComponents ?? 0).Append(",\n");
            sb.Append("  \"state_fields\": ").Append(m_sampler?.TrackedFields ?? 0).Append(",\n");
            sb.Append("  \"hooked_unity_events\": ").Append(m_tap?.HookedEvents ?? 0).Append(",\n");
            if (rig != null && rig.Views.Count > 0)
                m_viewsJson = "[" + string.Join(", ", rig.Views.Select(v => "{\"id\": " + TelemetryFormat.Json(v.Id) + ", \"label\": " + TelemetryFormat.Json(v.Config.Label) + ", \"family\": " + TelemetryFormat.Json(v.Config.Family.ToString()) + "}")) + "]";
            sb.Append("  \"views\": ").Append(m_viewsJson).Append(",\n");
            sb.Append("  \"telemetry_columns\": [");
            sb.Append(string.Join(", ", m_row.Names.Select(TelemetryFormat.Json)));
            sb.Append("]\n}\n");
            try { File.WriteAllText(Path.Combine(SessionDirectory, "session.json"), sb.ToString()); }
            catch (Exception e) { Debug.LogWarning("[ControlRoom] could not write session.json: " + e.Message); }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        // ---------------------------------------------------------------- motion helpers

        private class MotionTracker
        {
            public Vector3 Velocity;
            public float Speed, AngularSpeed, YawRate;
            public bool Valid;
            private Vector3 m_lastPos;
            private Quaternion m_lastRot;
            private bool m_hasLast;

            public void Update(Transform t, float dt)
            {
                if (t == null) { Reset(); return; }
                Update(t.position, t.rotation, dt);
            }

            public void Update(Vector3 position, Quaternion rotation, float dt)
            {
                if (m_hasLast && dt > 1e-5f)
                {
                    Velocity = (position - m_lastPos) / dt;
                    Speed = Velocity.magnitude;
                    AngularSpeed = Quaternion.Angle(m_lastRot, rotation) / dt;
                    YawRate = Mathf.DeltaAngle(m_lastRot.eulerAngles.y, rotation.eulerAngles.y) / dt;
                    Valid = true;
                }
                m_lastPos = position;
                m_lastRot = rotation;
                m_hasLast = true;
            }

            public void Reset()
            {
                m_hasLast = false;
                Valid = false;
                Velocity = Vector3.zero;
            }
        }

        /// <summary>Tremor/jitter proxy: RMS distance of the position from its own low-pass filter (≈0.5 s window).</summary>
        private class JitterTracker
        {
            public float Rms = float.NaN;
            private Vector3 m_filtered;
            private float m_meanSquare;
            private bool m_init;

            public void Update(Transform t, float dt)
            {
                if (t == null) { m_init = false; Rms = float.NaN; return; }
                if (!m_init) { m_filtered = t.position; m_meanSquare = 0; m_init = true; }
                var k = 1f - Mathf.Exp(-dt / 0.15f);
                m_filtered = Vector3.Lerp(m_filtered, t.position, k);
                var e = (t.position - m_filtered).sqrMagnitude;
                m_meanSquare = Mathf.Lerp(m_meanSquare, e, 1f - Mathf.Exp(-dt / 0.5f));
                Rms = Mathf.Sqrt(m_meanSquare);
            }
        }
    }
}
