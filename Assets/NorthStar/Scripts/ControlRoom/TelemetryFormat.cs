// Assignment 3 / Task 1 - CSV/JSON helpers shared by the telemetry writers.
// All numbers use the invariant culture so files open the same way on every machine.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NorthStar.ControlRoom
{
    public static class TelemetryFormat
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string F(float value) => !float.IsNaN(value) && !float.IsInfinity(value) ? value.ToString("0.#####", Inv) : "";
        public static string F(double value) => !double.IsNaN(value) && !double.IsInfinity(value) ? value.ToString("0.#####", Inv) : "";
        public static string B(bool value) => value ? "1" : "0";

        public static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(s_csvSpecial) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static readonly char[] s_csvSpecial = { ',', '"', '\n', '\r' };

        public static string Json(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>
    /// One wide telemetry sample. Columns are declared by the order of Add calls; the first sample defines the header,
    /// so every Add must run unconditionally (write an empty value when a source is missing).
    /// </summary>
    public class TelemetryRow
    {
        public readonly List<string> Names = new();
        public readonly List<string> Values = new();

        public void Clear()
        {
            Names.Clear();
            Values.Clear();
        }

        public void Add(string name, string value) { Names.Add(name); Values.Add(value ?? ""); }
        public void Add(string name, float value) => Add(name, TelemetryFormat.F(value));
        public void Add(string name, double value) => Add(name, TelemetryFormat.F(value));
        public void Add(string name, int value) => Add(name, value.ToString(TelemetryFormat.Inv));
        public void Add(string name, bool value) => Add(name, TelemetryFormat.B(value));

        public void Add(string name, Vector3 v, bool valid = true)
        {
            Add(name + "_x", valid ? TelemetryFormat.F(v.x) : "");
            Add(name + "_y", valid ? TelemetryFormat.F(v.y) : "");
            Add(name + "_z", valid ? TelemetryFormat.F(v.z) : "");
        }

        public void Add(string name, Vector2 v, bool valid = true)
        {
            Add(name + "_x", valid ? TelemetryFormat.F(v.x) : "");
            Add(name + "_y", valid ? TelemetryFormat.F(v.y) : "");
        }

        public void Add(string name, Quaternion q, bool valid = true)
        {
            Add(name + "_qx", valid ? TelemetryFormat.F(q.x) : "");
            Add(name + "_qy", valid ? TelemetryFormat.F(q.y) : "");
            Add(name + "_qz", valid ? TelemetryFormat.F(q.z) : "");
            Add(name + "_qw", valid ? TelemetryFormat.F(q.w) : "");
        }

        public string HeaderLine()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Names.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(TelemetryFormat.Csv(Names[i]));
            }
            return sb.ToString();
        }

        public string CsvLine()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(TelemetryFormat.Csv(Values[i]));
            }
            return sb.ToString();
        }

        public string JsonObject(string type)
        {
            var sb = new StringBuilder("{\"type\":").Append(TelemetryFormat.Json(type));
            for (var i = 0; i < Names.Count; i++)
            {
                sb.Append(',').Append(TelemetryFormat.Json(Names[i])).Append(':');
                var value = Values[i];
                if (value.Length > 0 && double.TryParse(value, NumberStyles.Float, TelemetryFormat.Inv, out _))
                    sb.Append(value);
                else
                    sb.Append(TelemetryFormat.Json(value));
            }
            return sb.Append('}').ToString();
        }
    }

    /// <summary>Buffered CSV file. Flushed periodically by the recorder and on dispose.</summary>
    public sealed class CsvLog : IDisposable
    {
        private readonly StreamWriter m_writer;
        public string Path { get; }
        public long Rows { get; private set; }

        public CsvLog(string path, string header)
        {
            Path = path;
            m_writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
            if (header != null) m_writer.WriteLine(header);
        }

        public void WriteLine(string line)
        {
            m_writer.WriteLine(line);
            Rows++;
        }

        public void WriteFields(params string[] fields)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) m_writer.Write(',');
                m_writer.Write(TelemetryFormat.Csv(fields[i]));
            }
            m_writer.WriteLine();
            Rows++;
        }

        public void Flush() => m_writer.Flush();

        public void Dispose()
        {
            m_writer.Flush();
            m_writer.Dispose();
        }
    }
}
