// Assignment 3 / Task 1 - Shared scene references for the control room.
// Finds the player rig, boat and interactables once per scene (plus a slow periodic rescan for spawned objects)
// so the recorder and the camera rig never call FindObjectsByType every frame.

using System.Collections.Generic;
using System.Reflection;
using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NorthStar.ControlRoom
{
    public static class ControlRoomContext
    {
        private const float RESCAN_INTERVAL = 2f;

        public static Transform Head { get; private set; }
        public static Transform LeftHand { get; private set; }
        public static Transform RightHand { get; private set; }
        public static Transform TrackingSpace { get; private set; }
        public static Camera PlayerCamera { get; private set; }
        public static BoatController Boat { get; private set; }
        public static WheelController Wheel { get; private set; }
        public static SailAngleController Sail { get; private set; }
        /// <summary>Looked up directly: GameFlowController.Instance would create one as a side effect.</summary>
        public static GameFlowController GameFlow { get; private set; }

        public static readonly List<Grabbable> Grabbables = new();
        public static readonly List<BaseJointInteractable<float>> JointInteractables = new();
        public static readonly List<Harpoon> Harpoons = new();
        public static readonly List<NpcController> Npcs = new();
        public static readonly List<Subtitle> Subtitles = new();

        /// <summary>Grabbables currently held by at least one hand/controller (refreshed by <see cref="UpdateFrame"/>).</summary>
        public static readonly List<Grabbable> HeldGrabbables = new();

        /// <summary>Best guess at where the player's hands are doing something interesting this frame.</summary>
        public static Vector3 InteractionFocus { get; private set; }
        public static bool HasInteractionFocus { get; private set; }

        private static float s_lastScan = float.NegativeInfinity;
        private static int s_lastFrame = -1;
        private static bool s_hooked;

        private static readonly FieldInfo s_subtitleText =
            typeof(Subtitle).GetField("m_text", BindingFlags.Instance | BindingFlags.NonPublic);

        public static Vector3 SubjectPosition =>
            Head != null ? Head.position : (Boat != null ? Boat.transform.position : Vector3.zero);

        public static Quaternion BoatYaw => Boat != null ? YawOnly(Boat.transform.rotation) : Quaternion.identity;

        public static void ForceRescan() => s_lastScan = float.NegativeInfinity;

        /// <summary>Call once per frame from any consumer; repeated calls in the same frame are free.</summary>
        public static void UpdateFrame()
        {
            if (!s_hooked)
            {
                s_hooked = true;
                SceneManager.sceneLoaded += (_, _) => ForceRescan();
                SceneManager.sceneUnloaded += _ => ForceRescan();
            }

            if (s_lastFrame == Time.frameCount) return;
            s_lastFrame = Time.frameCount;

            if (Head == null || Time.unscaledTime - s_lastScan > RESCAN_INTERVAL)
            {
                Scan();
            }

            HeldGrabbables.Clear();
#pragma warning disable CS0618 // SelectingPointsCount is obsolete but is what NorthStar itself uses (BaseJointInteractable.IsGrabbing)
            foreach (var grabbable in Grabbables)
            {
                if (grabbable != null && grabbable.isActiveAndEnabled && grabbable.SelectingPointsCount > 0)
                {
                    HeldGrabbables.Add(grabbable);
                }
            }
#pragma warning restore CS0618

            if (HeldGrabbables.Count > 0)
            {
                var sum = Vector3.zero;
                foreach (var held in HeldGrabbables) sum += held.transform.position;
                InteractionFocus = sum / HeldGrabbables.Count;
                HasInteractionFocus = true;
            }
            else if (LeftHand != null && RightHand != null)
            {
                InteractionFocus = (LeftHand.position + RightHand.position) * 0.5f;
                HasInteractionFocus = true;
            }
            else
            {
                InteractionFocus = SubjectPosition;
                HasInteractionFocus = false;
            }
        }

        private static void Scan()
        {
            s_lastScan = Time.unscaledTime;

            var rig = Object.FindAnyObjectByType<OVRCameraRig>();
            if (rig != null)
            {
                Head = rig.centerEyeAnchor;
                LeftHand = rig.leftHandAnchor;
                RightHand = rig.rightHandAnchor;
                TrackingSpace = rig.trackingSpace;
                PlayerCamera = Head != null ? Head.GetComponent<Camera>() : null;
            }
            else
            {
                // Fallback for scenes without an OVR rig (e.g. the XR simulator off, loading screens).
                PlayerCamera = Camera.main;
                Head = PlayerCamera != null ? PlayerCamera.transform : null;
                LeftHand = RightHand = TrackingSpace = null;
            }

            Boat = BoatController.Instance;
            Wheel = Object.FindAnyObjectByType<WheelController>();
            Sail = Object.FindAnyObjectByType<SailAngleController>();
            GameFlow = Object.FindAnyObjectByType<GameFlowController>();

            Refill(Grabbables);
            Refill(JointInteractables);
            Refill(Harpoons);
            Refill(Npcs);
            Refill(Subtitles);
        }

        private static void Refill<T>(List<T> list) where T : Object
        {
            list.Clear();
            list.AddRange(Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID));
        }

        public static string GetSubtitleText(Subtitle subtitle)
        {
            if (subtitle == null || s_subtitleText == null) return "";
            var text = s_subtitleText.GetValue(subtitle) as TMP_Text;
            return text != null && text.isActiveAndEnabled ? text.text : "";
        }

        public static Quaternion YawOnly(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 1e-6f) forward = rotation * Vector3.up; // looking straight up/down
            forward.y = 0;
            return forward.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        public static string PathOf(Transform transform)
        {
            if (transform == null) return "";
            var path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }
            return path;
        }
    }
}
