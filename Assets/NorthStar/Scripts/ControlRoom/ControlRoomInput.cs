// Assignment 3 / Task 1 - Operator controls for testing the views before the Task 2 dashboard exists.
//
// Keyboard (click the Game view first so it has focus; also available from Window > NorthStar Control Room):
//   1..9, 0       put view #1..#10 on program        Tab / Shift+Tab   next / previous view
//   B             toggle hard cut <-> smooth blend    P                 save PNG of every view
//   M             toggle multiview overlay            K                 add a "mark" event to events.csv
//
// UDP commands on port 9051 (one per datagram, plain text) - the Task 2 dashboard can drive the same actions:
//   select <view_id> [cut|blend]     snapshot [view_id]     mark <free text>

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NorthStar.ControlRoom
{
    public class ControlRoomInput : MonoBehaviour
    {
        [SerializeField] private bool m_keyboardEnabled = true;
        [SerializeField] private bool m_udpCommandsEnabled = true;
        [SerializeField] private int m_udpCommandPort = 9051;

        private readonly ConcurrentQueue<string> m_commands = new();
        private UdpClient m_listener;
        private Thread m_thread;
        private volatile bool m_running;

        private void Start()
        {
            if (!m_udpCommandsEnabled) return;
            try
            {
                m_listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, m_udpCommandPort));
                m_running = true;
                m_thread = new Thread(Listen) { IsBackground = true, Name = "ControlRoomCommands" };
                m_thread.Start();
            }
            catch (SocketException e)
            {
                Debug.LogWarning($"[ControlRoom] command port {m_udpCommandPort} unavailable: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            m_running = false;
            m_listener?.Close(); // unblocks Receive
        }

        private void Listen()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (m_running)
            {
                try { m_commands.Enqueue(Encoding.UTF8.GetString(m_listener.Receive(ref any)).Trim()); }
                catch (SocketException) { if (!m_running) return; }
                catch (System.ObjectDisposedException) { return; }
            }
        }

        private void Update()
        {
            while (m_commands.TryDequeue(out var command)) Execute(command);
            if (m_keyboardEnabled) HandleKeyboard();
        }

        public void Execute(string command)
        {
            var rig = SpectatorCameraRig.Instance;
            var parts = command.Split(new[] { ' ' }, 3, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            switch (parts[0].ToLowerInvariant())
            {
                case "select" when parts.Length >= 2 && rig != null:
                    var kind = parts.Length >= 3 && parts[2].StartsWith("blend") ? TransitionKind.SmoothBlend
                        : parts.Length >= 3 ? TransitionKind.HardCut : CurrentTransition;
                    if (!rig.SelectView(parts[1], kind)) Debug.LogWarning("[ControlRoom] unknown view: " + parts[1]);
                    break;
                case "snapshot":
                    Snapshot(parts.Length >= 2 ? parts[1] : null);
                    break;
                case "mark":
                    Mark(parts.Length >= 2 ? command.Substring(5) : "");
                    break;
                default:
                    Debug.LogWarning("[ControlRoom] unknown command: " + command);
                    break;
            }
        }

        // ---------------------------------------------------------------- actions (keyboard, IMGUI, editor menu, overlay clicks)

        public static void SelectIndex(int index)
        {
            var rig = SpectatorCameraRig.Instance;
            if (rig == null) { Debug.LogWarning("[ControlRoom] select ignored: no SpectatorCameraRig running"); return; }
            if (index < 0 || index >= rig.Views.Count) { Debug.LogWarning($"[ControlRoom] select ignored: no view #{index + 1}"); return; }
            rig.SelectView(rig.Views[index].Id, CurrentTransition);
            Debug.Log($"[ControlRoom] program -> {rig.Views[index].Id} ({CurrentTransition})");
        }

        public static void Step(int step)
        {
            var rig = SpectatorCameraRig.Instance;
            if (rig == null || rig.Views.Count == 0) return;
            var index = 0;
            for (var i = 0; i < rig.Views.Count; i++)
                if (rig.Views[i] == rig.ProgramView) index = i;
            SelectIndex((index + step + rig.Views.Count) % rig.Views.Count);
        }

        public static void ToggleTransition()
        {
            s_transition = s_transition == TransitionKind.HardCut ? TransitionKind.SmoothBlend : TransitionKind.HardCut;
            Debug.Log("[ControlRoom] transition: " + s_transition);
        }

        public static void ToggleOverlay()
        {
            var overlay = FindAnyObjectByType<MultiviewDebugOverlay>();
            if (overlay == null) { Debug.LogWarning("[ControlRoom] overlay toggle ignored: no MultiviewDebugOverlay running"); return; }
            overlay.Visible = !overlay.Visible;
            Debug.Log("[ControlRoom] multiview overlay " + (overlay.Visible ? "on" : "off"));
        }

        public static void Mark(string note) => ExhaustiveTelemetryRecorder.Instance?.LogEvent("Operator", "mark", note);

        public static TransitionKind CurrentTransition => s_transition;
        private static TransitionKind s_transition = TransitionKind.HardCut;

        // ---------------------------------------------------------------- keyboard

        // Keys can arrive through both the Input System and IMGUI in the same frame; handle each key once.
        private int m_handledFrame = -1;
        private readonly System.Collections.Generic.HashSet<KeyCode> m_handled = new();

        private void HandleKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            for (var i = 0; i < 10; i++)
            {
                var key = i == 9 ? Key.Digit0 : Key.Digit1 + i;
                if (keyboard[key].wasPressedThisFrame) HandleKey(i == 9 ? KeyCode.Alpha0 : KeyCode.Alpha1 + i, false);
            }
            if (keyboard.tabKey.wasPressedThisFrame) HandleKey(KeyCode.Tab, keyboard.shiftKey.isPressed);
            if (keyboard.bKey.wasPressedThisFrame) HandleKey(KeyCode.B, false);
            if (keyboard.pKey.wasPressedThisFrame) HandleKey(KeyCode.P, false);
            if (keyboard.kKey.wasPressedThisFrame) HandleKey(KeyCode.K, false);
            if (keyboard.mKey.wasPressedThisFrame) HandleKey(KeyCode.M, false);
        }

        // IMGUI receives Game-view key events regardless of Input System settings - a fallback path.
        private void OnGUI()
        {
            if (!m_keyboardEnabled) return;
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None) HandleKey(e.keyCode, e.shift);
        }

        private void HandleKey(KeyCode key, bool shift)
        {
            if (m_handledFrame != Time.frameCount)
            {
                m_handledFrame = Time.frameCount;
                m_handled.Clear();
            }
            if (!m_handled.Add(key)) return;

            if (key >= KeyCode.Alpha1 && key <= KeyCode.Alpha9) SelectIndex(key - KeyCode.Alpha1);
            else if (key == KeyCode.Alpha0) SelectIndex(9);
            else if (key == KeyCode.Tab) Step(shift ? -1 : 1);
            else if (key == KeyCode.B) ToggleTransition();
            else if (key == KeyCode.P) Snapshot(null);
            else if (key == KeyCode.K) Mark("keyboard");
            else if (key == KeyCode.M) ToggleOverlay();
        }

        public static void Snapshot(string viewId)
        {
            var rig = SpectatorCameraRig.Instance;
            var recorder = ExhaustiveTelemetryRecorder.Instance;
            if (rig == null || recorder == null) { Debug.LogWarning("[ControlRoom] snapshot ignored: control room not running"); return; }
            var written = rig.SaveSnapshot(Path.Combine(recorder.SessionDirectory, "snapshots"), viewId);
            recorder.LogEvent("Operator", "snapshot", written.Count + " file(s)");
            Debug.Log($"[ControlRoom] saved {written.Count} snapshot(s) to {Path.Combine(recorder.SessionDirectory, "snapshots")}");
        }
    }
}
