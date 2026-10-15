// Assignment 3 / Task 1 - "Capture the entire available state".
//
// The wide telemetry.csv covers the variables a director obviously needs. This sampler covers *everything else*:
// every field (public, [SerializeField] or private runtime state) of every NorthStar / Meta.Utilities component
// in the loaded scenes, plus every awake non-kinematic Rigidbody. Because the set of objects changes as scenes load
// and objects spawn, it is written in long format:
//
//   t_session, frame, kind, object_path, component, instance_id, field, value
//
// kind = S (full snapshot, written at start and every few seconds) or D (delta: value changed since last sample).
// Rebuilding the full state at any time t = last snapshot before t + all deltas up to t.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NorthStar.ControlRoom
{
    public class ReflectiveStateSampler
    {
        public const string HEADER = "t_session,frame,kind,object_path,component,instance_id,field,value";

        private static readonly string[] s_defaultNamespaces = { "NorthStar", "Meta.Utilities" };

        private class Tracked
        {
            public Component Component;
            public int InstanceId;
            public string Path;
            public string TypeName;
            public FieldInfo[] Fields;
            public string[] FieldNames;
            public string[] Last;
        }

        private static readonly Dictionary<Type, (FieldInfo[] fields, string[] names)> s_fieldCache = new();

        private readonly string[] m_namespaces;
        private readonly List<Tracked> m_tracked = new();
        private readonly HashSet<int> m_trackedIds = new();
        private readonly List<Rigidbody> m_bodies = new();
        private readonly Dictionary<int, string> m_lastBody = new();
        private readonly Dictionary<int, string> m_bodyPaths = new();

        public int TrackedComponents => m_tracked.Count;
        public int TrackedFields { get; private set; }
        public int TrackedBodies => m_bodies.Count;

        public ReflectiveStateSampler(string[] namespaces = null)
        {
            m_namespaces = namespaces is { Length: > 0 } ? namespaces : s_defaultNamespaces;
        }

        /// <summary>Find components in all loaded scenes (and DontDestroyOnLoad objects).</summary>
        public void Rescan()
        {
            m_tracked.RemoveAll(t => t.Component == null);
            m_trackedIds.Clear();
            foreach (var t in m_tracked) m_trackedIds.Add(t.InstanceId);

            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                if (!IsInScope(type)) continue;
                var id = behaviour.GetInstanceID();
                if (!m_trackedIds.Add(id)) continue;

                var (fields, names) = GetFields(type);
                if (fields.Length == 0) continue;

                m_tracked.Add(new Tracked
                {
                    Component = behaviour,
                    InstanceId = id,
                    Path = ControlRoomContext.PathOf(behaviour.transform),
                    TypeName = type.Name,
                    Fields = fields,
                    FieldNames = names,
                    Last = new string[fields.Length],
                });
            }

            TrackedFields = 0;
            foreach (var t in m_tracked) TrackedFields += t.Fields.Length;

            m_bodies.Clear();
            foreach (var body in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (body.isKinematic) continue;
                m_bodies.Add(body);
                m_bodyPaths[body.GetInstanceID()] = ControlRoomContext.PathOf(body.transform);
            }
        }

        private bool IsInScope(Type type) => IsInScope(type, m_namespaces);

        /// <summary>True for game/framework component types (excludes the control room itself).</summary>
        public static bool IsInScope(Type type, string[] namespaces)
        {
            var ns = type.Namespace;
            if (ns == null || ns.StartsWith("NorthStar.ControlRoom", StringComparison.Ordinal)) return false;
            foreach (var prefix in namespaces ?? s_defaultNamespaces)
            {
                if (ns.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Samples every tracked field; writes all values when <paramref name="snapshot"/> else only changes.</summary>
        public void Sample(CsvLog log, string tSession, string frame, bool snapshot)
        {
            var kind = snapshot ? "S" : "D";
            foreach (var t in m_tracked)
            {
                if (t.Component == null) continue;
                var id = t.InstanceId.ToString(TelemetryFormat.Inv);
                for (var i = 0; i < t.Fields.Length; i++)
                {
                    string value;
                    try { value = FormatValue(t.Fields[i].GetValue(t.Component)); }
                    catch (Exception e) { value = "<error:" + e.GetType().Name + ">"; }

                    if (!snapshot && value == t.Last[i]) continue;
                    t.Last[i] = value;
                    log.WriteFields(tSession, frame, kind, t.Path, t.TypeName, id, t.FieldNames[i], value);
                }
            }

            foreach (var body in m_bodies)
            {
                if (body == null || (body.IsSleeping() && !snapshot)) continue;
                var id = body.GetInstanceID();
                var value = FormatValue(body.position) + "|" + FormatValue(body.rotation.eulerAngles) + "|" + FormatValue(body.linearVelocity);
                if (!snapshot && m_lastBody.TryGetValue(id, out var last) && last == value) continue;
                m_lastBody[id] = value;
                m_bodyPaths.TryGetValue(id, out var path);
                log.WriteFields(tSession, frame, kind, path, "Rigidbody", id.ToString(TelemetryFormat.Inv), "position|euler|velocity", value);
            }
        }

        // ---------------------------------------------------------------- reflection

        private static (FieldInfo[] fields, string[] names) GetFields(Type type)
        {
            if (s_fieldCache.TryGetValue(type, out var cached)) return cached;

            var fields = new List<FieldInfo>();
            var names = new List<string>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!IsLoggable(field.FieldType)) continue;
                    fields.Add(field);
                    names.Add(CleanName(field.Name));
                }
            }

            cached = (fields.ToArray(), names.ToArray());
            s_fieldCache[type] = cached;
            return cached;
        }

        private static string CleanName(string name)
        {
            // [field: SerializeField] auto-properties compile to "<Name>k__BackingField".
            if (name.Length > 0 && name[0] == '<')
            {
                var end = name.IndexOf('>');
                if (end > 1) return name.Substring(1, end - 1);
            }
            return name;
        }

        private static bool IsLoggable(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return true;
            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion)
                || type == typeof(Color) || type == typeof(Vector2Int) || type == typeof(Vector3Int)) return true;
            if (typeof(Object).IsAssignableFrom(type)) return true;   // logged as the referenced object's name
            if (typeof(ICollection).IsAssignableFrom(type)) return true; // logged as element count
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return IsLoggable(underlying);
            // Delegates, events (UnityEvent is handled by UnityEventTap), animation curves and other big structs are skipped.
            return false;
        }

        public static string FormatValue(object value)
        {
            switch (value)
            {
                case null: return "null";
                case float f: return TelemetryFormat.F(f);
                case double d: return TelemetryFormat.F(d);
                case bool b: return TelemetryFormat.B(b);
                case string s: return s;
                case Vector3 v: return TelemetryFormat.F(v.x) + ";" + TelemetryFormat.F(v.y) + ";" + TelemetryFormat.F(v.z);
                case Vector2 v: return TelemetryFormat.F(v.x) + ";" + TelemetryFormat.F(v.y);
                case Vector4 v: return TelemetryFormat.F(v.x) + ";" + TelemetryFormat.F(v.y) + ";" + TelemetryFormat.F(v.z) + ";" + TelemetryFormat.F(v.w);
                case Quaternion q: return TelemetryFormat.F(q.x) + ";" + TelemetryFormat.F(q.y) + ";" + TelemetryFormat.F(q.z) + ";" + TelemetryFormat.F(q.w);
                case Color c: return TelemetryFormat.F(c.r) + ";" + TelemetryFormat.F(c.g) + ";" + TelemetryFormat.F(c.b) + ";" + TelemetryFormat.F(c.a);
                case Object o: return o == null ? "null" : o.name; // Unity "fake null" for destroyed objects
                case ICollection collection: return "count=" + collection.Count.ToString(TelemetryFormat.Inv);
                case IFormattable formattable: return formattable.ToString(null, TelemetryFormat.Inv);
                default: return value.ToString();
            }
        }
    }
}
