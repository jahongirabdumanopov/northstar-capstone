// Assignment 3 / Task 1 - Generic game-event capture.
// Instead of hand-wiring a few callbacks (Assignment 2: SteeringGrabbed, HarpoonFired, ...), this listens to
// *every* UnityEvent field on every NorthStar / Meta.Utilities component: harpoon fire/reload/rearm, button presses,
// lever/crank/slider changes, dialogue section completion, scene-change completion, task handlers, triggers, etc.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace NorthStar.ControlRoom
{
    public class UnityEventTap
    {
        /// <summary>(component, event field name, formatted arguments)</summary>
        public event Action<Component, string, string> Fired;

        /// <summary>Repeated firings of the same event within this window are counted but not reported (value-change spam).</summary>
        public float RepeatWindow = 0.2f;

        public int HookedEvents => m_hooked.Count;

        private readonly string[] m_namespaces;
        private readonly HashSet<(int, string)> m_hooked = new();
        private readonly Dictionary<(int, string), float> m_lastFired = new();
        private readonly Dictionary<(int, string), int> m_suppressed = new();
        private readonly Dictionary<(int, string), string> m_lastArgs = new();

        private static readonly Dictionary<Type, FieldInfo[]> s_eventFields = new();
        private static readonly MethodInfo s_hook1 = typeof(UnityEventTap).GetMethod(nameof(Hook1), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo s_hook2 = typeof(UnityEventTap).GetMethod(nameof(Hook2), BindingFlags.Instance | BindingFlags.NonPublic);

        public UnityEventTap(string[] namespaces = null) => m_namespaces = namespaces;

        public void Rescan()
        {
            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                if (!ReflectiveStateSampler.IsInScope(type, m_namespaces)) continue;

                foreach (var field in GetEventFields(type))
                {
                    var key = (behaviour.GetInstanceID(), field.Name);
                    if (m_hooked.Contains(key)) continue;
                    if (field.GetValue(behaviour) is not UnityEventBase evt) continue;
                    if (Hook(evt, behaviour, CleanName(field.Name))) m_hooked.Add(key);
                }
            }
        }

        private bool Hook(UnityEventBase evt, Component owner, string name)
        {
            if (evt is UnityEvent plain)
            {
                plain.AddListener(() => Raise(owner, name, ""));
                return true;
            }

            for (var t = evt.GetType(); t != null && t != typeof(UnityEventBase); t = t.BaseType)
            {
                if (!t.IsGenericType) continue;
                var definition = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                if (definition == typeof(UnityEvent<>))
                {
                    s_hook1.MakeGenericMethod(args).Invoke(this, new object[] { evt, owner, name });
                    return true;
                }
                if (definition == typeof(UnityEvent<,>))
                {
                    s_hook2.MakeGenericMethod(args).Invoke(this, new object[] { evt, owner, name });
                    return true;
                }
            }
            return false; // 3+ argument events are not used by NorthStar
        }

        private void Hook1<T>(UnityEvent<T> evt, Component owner, string name) =>
            evt.AddListener(a => Raise(owner, name, ReflectiveStateSampler.FormatValue(a)));

        private void Hook2<T0, T1>(UnityEvent<T0, T1> evt, Component owner, string name) =>
            evt.AddListener((a, b) => Raise(owner, name, ReflectiveStateSampler.FormatValue(a) + "|" + ReflectiveStateSampler.FormatValue(b)));

        private void Raise(Component owner, string name, string args)
        {
            if (owner == null) return;
            var key = (owner.GetInstanceID(), name);
            var now = Time.unscaledTime;

            // Value-change events (sliders, levers, cranks) fire every frame even when nothing moves;
            // only report them when the value actually changed.
            if (args.Length > 0)
            {
                if (m_lastArgs.TryGetValue(key, out var previous) && previous == args) return;
                m_lastArgs[key] = args;
            }
            if (m_lastFired.TryGetValue(key, out var last) && now - last < RepeatWindow)
            {
                m_suppressed[key] = m_suppressed.TryGetValue(key, out var n) ? n + 1 : 1;
                return;
            }
            m_lastFired[key] = now;
            if (m_suppressed.TryGetValue(key, out var skipped) && skipped > 0)
            {
                args += (args.Length > 0 ? " " : "") + "(+" + skipped + " repeats)";
                m_suppressed[key] = 0;
            }
            Fired?.Invoke(owner, name, args);
        }

        private static FieldInfo[] GetEventFields(Type type)
        {
            if (s_eventFields.TryGetValue(type, out var cached)) return cached;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(UnityEventBase).IsAssignableFrom(field.FieldType)) list.Add(field);
                }
            }
            cached = list.ToArray();
            s_eventFields[type] = cached;
            return cached;
        }

        private static string CleanName(string name)
        {
            if (name.Length > 0 && name[0] == '<')
            {
                var end = name.IndexOf('>');
                if (end > 1) return name.Substring(1, end - 1);
            }
            return name.StartsWith("m_", StringComparison.Ordinal) ? name.Substring(2) : name;
        }
    }
}
