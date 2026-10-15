// Assignment 3 / Task 1 - Dockable control room panel (Window > NorthStar Control Room > Open Panel).

using UnityEditor;
using UnityEngine;

namespace NorthStar.ControlRoom.Editor
{
    /// <summary>Dockable panel: works without Game-view focus, shortcuts or the menu bar.</summary>
    public class ControlRoomPanel : EditorWindow
    {
        private Vector2 m_scroll;
        private string m_markNote = "";

        public static void Open()
        {
            var window = GetWindow<ControlRoomPanel>();
            window.titleContent = new GUIContent("Control Room");
            window.minSize = new Vector2(260, 300);
            window.Show();
        }

        private void OnInspectorUpdate() => Repaint(); // ~10 Hz refresh of live status

        private void OnGUI()
        {
            var rig = SpectatorCameraRig.Instance;
            var recorder = ExhaustiveTelemetryRecorder.Instance;

            if (!EditorApplication.isPlaying || rig == null)
            {
                EditorGUILayout.HelpBox("Press Play. The control room starts automatically in every NorthStar scene.", MessageType.Info);
                return;
            }

            if (recorder != null)
            {
                EditorGUILayout.LabelField("Recording", $"{recorder.SessionTime:0}s · {recorder.SampleCount} samples");
                EditorGUILayout.LabelField("Program", rig.ProgramViewId + (rig.IsBlending ? " (blending)" : ""));
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                var blend = ControlRoomInput.CurrentTransition == TransitionKind.SmoothBlend;
                if (GUILayout.Toggle(!blend, "Hard cut", EditorStyles.miniButtonLeft) && blend) ControlRoomInput.ToggleTransition();
                if (GUILayout.Toggle(blend, "Smooth blend", EditorStyles.miniButtonRight) && !blend) ControlRoomInput.ToggleTransition();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Put view on program", EditorStyles.boldLabel);
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            for (var i = 0; i < rig.Views.Count; i++)
            {
                var view = rig.Views[i];
                var onProgram = view == rig.ProgramView;
                var status = view.SubjectVisible ? "" : "  ⚠ occluded";
                var previous = GUI.backgroundColor;
                if (onProgram) GUI.backgroundColor = new Color(1f, 0.45f, 0.45f);
                if (GUILayout.Button($"{i + 1}. {view.Config.Label}{status}", GUILayout.Height(22)))
                    ControlRoomInput.SelectIndex(i);
                GUI.backgroundColor = previous;
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Snapshot all")) ControlRoomInput.Snapshot(null);
                if (GUILayout.Button("Multiview overlay")) ControlRoomInput.ToggleOverlay();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                m_markNote = EditorGUILayout.TextField(m_markNote);
                if (GUILayout.Button("Mark", GUILayout.Width(60)))
                {
                    ControlRoomInput.Mark(string.IsNullOrWhiteSpace(m_markNote) ? "panel" : m_markNote);
                    m_markNote = "";
                }
            }
            if (recorder != null && GUILayout.Button("Open session folder")) EditorUtility.RevealInFinder(recorder.SessionDirectory);
        }
    }
}
