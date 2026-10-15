// Assignment 3 / Task 1 - In-editor proof that every view renders: a grid of all monitors over the Game view.
// Press M (Game view focused) or Cmd/Ctrl+Alt+M to toggle; click a tile to put that view on program. The real multiview belongs to the Task 2 dashboard; this is for testing and report screenshots.

using UnityEngine;

namespace NorthStar.ControlRoom
{
    public class MultiviewDebugOverlay : MonoBehaviour
    {
        public bool Visible;

        private GUIStyle m_label;
        private Texture2D m_white;

        private void OnGUI()
        {
            var rig = SpectatorCameraRig.Instance;
            if (!Visible || rig == null || rig.Views.Count == 0) return;

            m_white ??= Texture2D.whiteTexture;
            m_label ??= new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };

            var count = rig.Views.Count;
            var columns = Mathf.CeilToInt(Mathf.Sqrt(count * 16f / 9f * Screen.height / Screen.width));
            columns = Mathf.Clamp(columns, 1, count);
            var rows = Mathf.CeilToInt(count / (float)columns);
            var cellW = Screen.width / (float)columns;
            var cellH = Screen.height / (float)rows;

            GUI.color = new Color(0, 0, 0, 0.85f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), m_white);
            GUI.color = Color.white;

            var recorder = ExhaustiveTelemetryRecorder.Instance;
            for (var i = 0; i < count; i++)
            {
                var view = rig.Views[i];
                var cell = new Rect(i % columns * cellW, i / columns * cellH, cellW, cellH);
                var inner = new Rect(cell.x + 3, cell.y + 3, cell.width - 6, cell.height - 6);

                if (view == rig.ProgramView)
                {
                    GUI.color = Color.red;
                    GUI.DrawTexture(cell, m_white);
                    GUI.color = Color.white;
                }

                if (view.Monitor != null) GUI.DrawTexture(inner, view.Monitor, ScaleMode.ScaleToFit, false);

                // Click a tile to put it on program.
                var e = Event.current;
                if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                {
                    rig.SelectView(view.Id, ControlRoomInput.CurrentTransition);
                    e.Use();
                }

                var status = view.SubjectVisible ? "visible" : $"OCCLUDED {view.OccludedSeconds:0.0}s";
                var hotkey = i < 10 ? $"[{(i + 1) % 10}] " : "";
                GUI.Label(new Rect(inner.x + 6, inner.y + 4, inner.width - 12, 20), $"{hotkey}{view.Config.Label}", m_label);
                GUI.Label(new Rect(inner.x + 6, inner.yMax - 22, inner.width - 12, 20), $"{view.Id} · {status} · {view.SubjectDistance:0.0} m", m_label);
            }

            if (recorder != null)
            {
                GUI.Label(new Rect(8, Screen.height - 22, Screen.width - 16, 20),
                    $"REC {recorder.SessionTime:0}s · {recorder.SampleCount} samples · program: {rig.ProgramViewId} · {recorder.SessionDirectory}", m_label);
            }
        }
    }
}
