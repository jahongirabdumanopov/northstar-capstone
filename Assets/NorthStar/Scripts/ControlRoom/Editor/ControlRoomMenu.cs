// Assignment 3 / Task 1 - Editor controls for the control room.
//
// Window > NorthStar Control Room > Open Panel   opens a dockable panel with a button per view, cut/blend toggle,
//                                                snapshot, mark, multiview overlay and the live recording status.
// The same actions are menu items under Window > NorthStar Control Room (shortcuts Cmd/Ctrl+Alt + M, N, B, T, Y, K).
// They live under the built-in Window menu because a custom top-level menu can be hidden when the macOS menu bar is full.

using UnityEditor;
using UnityEngine;

namespace NorthStar.ControlRoom.Editor
{
    public static class ControlRoomMenu
    {
        private const string ROOT = "Window/NorthStar Control Room/";

        private static bool Playing() => EditorApplication.isPlaying && SpectatorCameraRig.Instance != null;

        [MenuItem(ROOT + "Open Panel", false, 0)]
        private static void OpenPanel() => ControlRoomPanel.Open();

        [MenuItem(ROOT + "Toggle Multiview Overlay %&m", false, 20)]
        private static void ToggleOverlay() => ControlRoomInput.ToggleOverlay();

        [MenuItem(ROOT + "Next View %&n", false, 21)]
        private static void Next() => ControlRoomInput.Step(1);

        [MenuItem(ROOT + "Previous View %&b", false, 22)]
        private static void Previous() => ControlRoomInput.Step(-1);

        [MenuItem(ROOT + "Toggle Hard Cut - Smooth Blend %&t", false, 23)]
        private static void ToggleTransition() => ControlRoomInput.ToggleTransition();

        [MenuItem(ROOT + "Save Snapshot of All Views %&y", false, 40)]
        private static void Snapshot() => ControlRoomInput.Snapshot(null);

        [MenuItem(ROOT + "Add Mark Event %&k", false, 41)]
        private static void Mark() => ControlRoomInput.Mark("editor menu");

        [MenuItem(ROOT + "Open Session Folder", false, 60)]
        private static void OpenFolder()
        {
            var recorder = ExhaustiveTelemetryRecorder.Instance;
            if (recorder != null) EditorUtility.RevealInFinder(recorder.SessionDirectory);
        }

        [MenuItem(ROOT + "Toggle Multiview Overlay %&m", true)]
        [MenuItem(ROOT + "Next View %&n", true)]
        [MenuItem(ROOT + "Previous View %&b", true)]
        [MenuItem(ROOT + "Toggle Hard Cut - Smooth Blend %&t", true)]
        [MenuItem(ROOT + "Save Snapshot of All Views %&y", true)]
        [MenuItem(ROOT + "Add Mark Event %&k", true)]
        [MenuItem(ROOT + "Open Session Folder", true)]
        private static bool Validate() => Playing();
    }
}
