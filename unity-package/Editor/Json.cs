using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dsh.UnityBridge
{
    /// <summary>
    /// Minimal dependency-free JSON reader/writer with Unity aware value conversion.
    /// Unity 2022.3 has no System.Text.Json in the editor profile, so the bridge carries
    /// its own parser to support free-form "params" objects and arbitrary eval results.
    /// </summary>
    internal static class Json
    {
        const int MaxDepth = 12;

        // ------------------------------------------------------------------ parse

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON input is null");
            int i = 0;
            object value = ParseValue(text, ref i, 0);
            SkipWhitespace(text, ref i);
            if (i < text.Length)
                throw new FormatException("Unexpected trailing character at position " + i + ": '" + text[i] + "'");
            return value;
        }

        static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException("JSON nested too deeply");
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON input");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("Invalid JSON literal at position " + i);
            i += literal.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new FormatException("Expected object key at position " + i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new FormatException("Expected ':' at position " + i);
                i++;
                result[key] = ParseValue(s, ref i, depth + 1);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}' at position " + i);
            }
        }

        static List<object> ParseArray(string s, ref int i, int depth)
        {
            var result = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return result;
            }
            while (true)
            {
                result.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']' at position " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Truncated unicode escape");
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("Invalid escape '\\" + e + "'");
                }
            }
            throw new FormatException("Unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            bool isFloat = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; i++; continue; }
                break;
            }
            if (i == start) throw new FormatException("Invalid number at position " + start);
            string token = s.Substring(start, i - start);
            if (!isFloat)
            {
                long l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            }
            double d;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("Invalid number '" + token + "'");
        }

        static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        // ---------------------------------------------------------------- serialize

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            if (depth > MaxDepth)
            {
                WriteString(sb, SafeToString(value));
                return;
            }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }
            if (value is char) { WriteString(sb, value.ToString()); return; }
            if (value is Enum) { WriteString(sb, value.ToString()); return; }
            if (value is float)
            {
                float f = (float)value;
                if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                if (Math.Abs(d) < 1e15 && Math.Floor(d) == d) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is decimal) { sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            var unityObject = value as Object;
            if (unityObject != null)
            {
                WriteValue(sb, Describe(unityObject), depth + 1);
                return;
            }

            var vector3 = TryVector3(value);
            if (vector3 != null) { WriteValue(sb, vector3, depth + 1); return; }
            var quaternion = TryQuaternion(value);
            if (quaternion != null) { WriteValue(sb, quaternion, depth + 1); return; }
            var color = TryColor(value);
            if (color != null) { WriteValue(sb, color, depth + 1); return; }

            var dict = value as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null && !(value is byte[]))
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, SafeToString(value));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static string SafeToString(object value)
        {
            try { return value == null ? "null" : value.ToString(); }
            catch (Exception e) { return "<ToString failed: " + e.GetType().Name + ">"; }
        }

        // -------------------------------------------------- unity aware conversions

        /// <summary>Plain CLR projection: safe to hand to another thread for serialization.</summary>
        public static object ToPlain(object value)
        {
            return ToPlain(value, 0);
        }

        static object ToPlain(object value, int depth)
        {
            if (value == null) return null;
            if (depth > MaxDepth) return SafeToString(value);
            if (value is string) return value;
            if (value is bool) return value;
            if (value is Enum) return value.ToString();
            if (value is float || value is double || value is decimal) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is ulong) return ((ulong)value).ToString(CultureInfo.InvariantCulture);

            var unityObject = value as Object;
            if (unityObject != null) return Describe(unityObject);

            var vector3 = TryVector3(value);
            if (vector3 != null) return vector3;
            var quaternion = TryQuaternion(value);
            if (quaternion != null) return quaternion;
            var color = TryColor(value);
            if (color != null) return color;

            var dict = value as IDictionary;
            if (dict != null)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dict)
                    result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = ToPlain(entry.Value, depth + 1);
                return result;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null && !(value is byte[]))
            {
                var list = new List<object>();
                foreach (object item in enumerable) list.Add(ToPlain(item, depth + 1));
                return list;
            }

            if (value is byte[]) return "<byte[" + ((byte[])value).Length + "]>";

            // Anonymous types and plain C# objects are the usual eval results; project their
            // readable members instead of collapsing the whole value into ToString().
            var poco = ToPlainObject(value, depth);
            if (poco != null) return poco;
            return SafeToString(value);
        }

        /// <summary>Projects a plain CLR object's public members, or returns null when it is not one.</summary>
        static Dictionary<string, object> ToPlainObject(object value, int depth)
        {
            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type.IsArray || type.IsPointer) return null;
            if (type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal)) return null;

            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                d[field.Name] = ToPlain(field.GetValue(value), depth + 1);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetGetMethod() == null) continue;
                object member;
                try { member = property.GetValue(value, null); }
                catch (Exception) { continue; }
                d[property.Name] = ToPlain(member, depth + 1);
            }
            return d.Count == 0 ? null : d;
        }

        static Dictionary<string, object> Describe(Object uo)
        {
            if (uo == null) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["name"] = uo.name;
            d["type"] = uo.GetType().Name;

            var go = uo as GameObject;
            if (go != null)
            {
                d["instanceId"] = go.GetInstanceID();
                d["path"] = SceneSearch.PathOf(go);
                d["activeSelf"] = go.activeSelf;
                d["activeInHierarchy"] = go.activeInHierarchy;
                d["scene"] = go.scene.IsValid() ? go.scene.name : null;
                return d;
            }

            var component = uo as Component;
            if (component != null)
            {
                d["instanceId"] = component.GetInstanceID();
                d["gameObject"] = SceneSearch.PathOf(component.gameObject);
                d["path"] = SceneSearch.PathOf(component.gameObject) + ":" + component.GetType().Name;
                return d;
            }

            d["instanceId"] = uo.GetInstanceID();
            return d;
        }

        static Dictionary<string, object> TryVector3(object value)
        {
            if (value is Vector3)
            {
                var v = (Vector3)value;
                return new Dictionary<string, object> { { "x", (double)v.x }, { "y", (double)v.y }, { "z", (double)v.z } };
            }
            if (value is Vector2)
            {
                var v = (Vector2)value;
                return new Dictionary<string, object> { { "x", (double)v.x }, { "y", (double)v.y } };
            }
            if (value is Vector4)
            {
                var v = (Vector4)value;
                return new Dictionary<string, object> { { "x", (double)v.x }, { "y", (double)v.y }, { "z", (double)v.z }, { "w", (double)v.w } };
            }
            if (value is Quaternion) return null;
            return null;
        }

        static Dictionary<string, object> TryQuaternion(object value)
        {
            if (!(value is Quaternion)) return null;
            var q = (Quaternion)value;
            var euler = q.eulerAngles;
            return new Dictionary<string, object>
            {
                { "x", (double)q.x }, { "y", (double)q.y }, { "z", (double)q.z }, { "w", (double)q.w },
                { "euler", new Dictionary<string, object> { { "x", (double)euler.x }, { "y", (double)euler.y }, { "z", (double)euler.z } } }
            };
        }

        static Dictionary<string, object> TryColor(object value)
        {
            if (value is Color)
            {
                var c = (Color)value;
                return new Dictionary<string, object> { { "r", (double)c.r }, { "g", (double)c.g }, { "b", (double)c.b }, { "a", (double)c.a } };
            }
            if (value is Color32)
            {
                var c = (Color32)value;
                return new Dictionary<string, object> { { "r", (int)c.r }, { "g", (int)c.g }, { "b", (int)c.b }, { "a", (int)c.a } };
            }
            return null;
        }
    }
}
