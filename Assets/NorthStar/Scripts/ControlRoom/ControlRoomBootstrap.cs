// Assignment 3 / Task 1 - Control room bootstrap.
// Spawns the exhaustive telemetry recorder and the spectator camera rig once per play session,
// so every NorthStar scene (Launch, Beat1..7, FreeSailing) is covered without editing scene files.

using UnityEngine;
using UnityEngine.SceneManagement;

namespace NorthStar.ControlRoom
{
    public static class ControlRoomBootstrap
    {
        /// <summary>Set to false (e.g. from an editor script or a test) to stop the automatic spawn.</summary>
        public static bool AutoSpawn = true;

        public static GameObject Root { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (!AutoSpawn || Root != null) return;

            // A scene may already contain a hand-placed rig with custom settings; respect it.
            var existingRecorder = Object.FindAnyObjectByType<ExhaustiveTelemetryRecorder>();
            if (existingRecorder != null)
            {
                Root = existingRecorder.gameObject;
                Object.DontDestroyOnLoad(Root.transform.root.gameObject);
                return;
            }

            Root = new GameObject("[ControlRoom]");
            Object.DontDestroyOnLoad(Root);
            Root.AddComponent<SpectatorCameraRig>();
            Root.AddComponent<ExhaustiveTelemetryRecorder>();
            Root.AddComponent<ControlRoomInput>();
            Root.AddComponent<MultiviewDebugOverlay>();

            SceneManager.sceneLoaded += (_, _) => DisableLegacyDirector();
            DisableLegacyDirector();
        }

        /// <summary>
        /// The Assignment 2 prototype (NorthStarTelemetry) switches cameras itself based on steering.
        /// Two directors fighting over the output would corrupt both the program feed and the logs,
        /// so it is turned off while the control room is running. Its cameras are adopted as extra views.
        /// </summary>
        private static void DisableLegacyDirector()
        {
            foreach (var legacy in Object.FindObjectsByType<NorthStarTelemetry>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!legacy.enabled) continue;
                legacy.enabled = false;
                Debug.Log("[ControlRoom] Disabled legacy NorthStarTelemetry (Assignment 2); ExhaustiveTelemetryRecorder replaces it.");
            }
        }
    }
}
