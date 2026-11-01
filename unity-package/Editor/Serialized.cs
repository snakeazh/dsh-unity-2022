using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Dsh.UnityBridge
{
    /// <summary>Reads and writes serialized properties without needing per-type C# code.</summary>
    internal static class Serialized
    {
        const int MaxTraversal = 4000;
        const int MaxTextLength = 240;

        // ------------------------------------------------------------------ read

        internal static List<object> ReadProperties(Component component, int budget)
        {
            var result = new List<object>();
            if (component == null || budget <= 0) return result;

            SerializedObject so;
            try { so = new SerializedObject(component); }
            catch (Exception) { return result; }

            SerializedProperty it = so.GetIterator();
            bool has = it.NextVisible(true);
            int guard = 0;
            while (has && guard++ < MaxTraversal)
            {
                if (result.Count >= budget) break;
                ReadProperty(it, result, budget, 0);
                has = it.NextVisible(false);
            }
            return result;
        }

        static void ReadProperty(SerializedProperty p, List<object> into, int budget, int depth)
        {
            if (into.Count >= budget) return;

            if (p.propertyType == SerializedPropertyType.Generic && p.hasVisibleChildren && depth < 2)
            {
                ReadChildren(p, into, budget, depth);
                return;
            }

            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["name"] = string.IsNullOrEmpty(p.displayName) ? p.name : p.displayName;
            entry["path"] = p.propertyPath;
            entry["type"] = p.propertyType.ToString();

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    entry["value"] = p.type == "long" ? (object)p.longValue : (long)p.intValue;
                    break;
                case SerializedPropertyType.Boolean:
                    entry["value"] = p.boolValue;
                    break;
                case SerializedPropertyType.Float:
                    entry["value"] = (double)p.floatValue;
                    break;
                case SerializedPropertyType.String:
                    entry["value"] = Clip(p.stringValue);
                    break;
                case SerializedPropertyType.Enum:
                    entry["value"] = EnumName(p);
                    entry["enumIndex"] = p.enumValueIndex;
                    break;
                case SerializedPropertyType.LayerMask:
                    entry["value"] = (long)p.intValue;
                    break;
                case SerializedPropertyType.Vector2:
                    entry["value"] = Flipped(p.vector2Value);
                    break;
                case SerializedPropertyType.Vector3:
                    entry["value"] = Flipped(p.vector3Value);
                    break;
                case SerializedPropertyType.Vector4:
                    entry["value"] = Flipped(p.vector4Value);
                    break;
                case SerializedPropertyType.Vector2Int:
                    Vector2Int v2i = p.vector2IntValue;
                    entry["value"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "x", (long)v2i.x }, { "y", (long)v2i.y } };
                    break;
                case SerializedPropertyType.Vector3Int:
                    Vector3Int v3i = p.vector3IntValue;
                    entry["value"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "x", (long)v3i.x }, { "y", (long)v3i.y }, { "z", (long)v3i.z } };
                    break;
                case SerializedPropertyType.Quaternion:
                    entry["value"] = Flipped(p.quaternionValue);
                    break;
                case SerializedPropertyType.Color:
                    entry["value"] = ColorDict(p.colorValue);
                    break;
                case SerializedPropertyType.Rect:
                    Rect r = p.rectValue;
                    entry["value"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "x", (double)r.x }, { "y", (double)r.y }, { "width", (double)r.width }, { "height", (double)r.height }
                    };
                    break;
                case SerializedPropertyType.Bounds:
                    entry["value"] = BoundsDict(p.boundsValue);
                    break;
                case SerializedPropertyType.ObjectReference:
                    UnityEngine.Object reference = p.objectReferenceValue;
                    entry["value"] = ReferenceDict(reference);
                    entry["isNull"] = reference == null;
                    break;
                case SerializedPropertyType.ArraySize:
                    entry["value"] = (long)p.intValue;
                    entry["arraySize"] = true;
                    break;
                case SerializedPropertyType.Character:
                    entry["value"] = (long)p.intValue;
                    break;
                default:
                    return;
            }
            into.Add(entry);
        }

        static void ReadChildren(SerializedProperty parent, List<object> into, int budget, int depth)
        {
            SerializedProperty p = parent.Copy();
            int parentDepth = parent.depth;
            bool has = p.NextVisible(true);
            int guard = 0;
            while (has && guard++ < MaxTraversal)
            {
                if (p.depth != parentDepth + 1) break;
                if (into.Count >= budget) break;
                ReadProperty(p, into, budget, depth + 1);
                has = p.NextVisible(false);
            }
        }

        static string EnumName(SerializedProperty p)
        {
            string[] names = p.enumNames;
            int index = p.enumValueIndex;
            if (names != null && index >= 0 && index < names.Length) return names[index];
            return index.ToString(CultureInfo.InvariantCulture);
        }

        static string Clip(string value)
        {
            if (value == null) return null;
            if (value.Length <= MaxTextLength) return value;
            return value.Substring(0, MaxTextLength) + "…";
        }

        static Dictionary<string, object> Flipped(Vector2 v)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "x", (double)v.x }, { "y", (double)v.y } };
        }

        static Dictionary<string, object> Flipped(Vector3 v)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "x", (double)v.x }, { "y", (double)v.y }, { "z", (double)v.z } };
        }

        static Dictionary<string, object> Flipped(Vector4 v)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "x", (double)v.x }, { "y", (double)v.y }, { "z", (double)v.z }, { "w", (double)v.w }
            };
        }

        static Dictionary<string, object> Flipped(Quaternion q)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "x", (double)q.x }, { "y", (double)q.y }, { "z", (double)q.z }, { "w", (double)q.w }
            };
        }

        static Dictionary<string, object> ColorDict(Color c)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "r", (double)c.r }, { "g", (double)c.g }, { "b", (double)c.b }, { "a", (double)c.a }
            };
        }

        static Dictionary<string, object> BoundsDict(Bounds b)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "center", Flipped(b.center) }, { "size", Flipped(b.size) }
            };
        }

        internal static Dictionary<string, object> ReferenceDict(UnityEngine.Object reference)
        {
            if (reference == null) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["name"] = reference.name;
            d["type"] = reference.GetType().Name;
            d["instanceId"] = reference.GetInstanceID();
            string assetPath = AssetDatabase.Contains(reference) ? AssetDatabase.GetAssetPath(reference) : null;
            if (!string.IsNullOrEmpty(assetPath)) d["assetPath"] = assetPath;
            GameObject go = reference as GameObject;
            if (go != null) d["path"] = SceneSearch.PathOf(go);
            else
            {
                Component component = reference as Component;
                if (component != null && component.gameObject != null) d["path"] = SceneSearch.PathOf(component.gameObject) + ":" + component.GetType().Name;
            }
            return d;
        }

        // ----------------------------------------------------------------- write

        internal static List<object> ApplyProperties(Component component, Dictionary<string, object> values, out int applied)
        {
            var results = new List<object>();
            applied = 0;
            if (component == null) throw new BridgeException("invalid_target", "Component is null.");
            if (values == null || values.Count == 0) return results;

            var so = new SerializedObject(component);
            so.Update();

            foreach (KeyValuePair<string, object> pair in values)
            {
                var entry = new Dictionary<string, object>(StringComparer.Ordinal);
                entry["key"] = pair.Key;
                try
                {
                    SerializedProperty property = Find(so, pair.Key);
                    if (property == null)
                    {
                        entry["ok"] = false;
                        entry["error"] = "No serialized field matches '" + pair.Key + "'.";
                        results.Add(entry);
                        continue;
                    }
                    entry["path"] = property.propertyPath;
                    entry["type"] = property.propertyType.ToString();
                    SetValue(property, pair.Value);
                    entry["ok"] = true;
                    applied++;
                }
                catch (BridgeException e)
                {
                    entry["ok"] = false;
                    entry["error"] = e.Message;
                }
                catch (Exception e)
                {
                    entry["ok"] = false;
                    entry["error"] = e.GetType().Name + ": " + e.Message;
                }
                results.Add(entry);
            }

            so.ApplyModifiedProperties();
            so.Update();
            return results;
        }

        static SerializedProperty Find(SerializedObject so, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            SerializedProperty direct = so.FindProperty(key);
            if (direct != null) return direct;

            string withoutPrefix = key.StartsWith("m_", StringComparison.Ordinal) ? key.Substring(2) : "m_" + key;
            direct = so.FindProperty(withoutPrefix);
            if (direct != null) return direct;

            SerializedProperty it = so.GetIterator();
            bool has = it.NextVisible(true);
            int guard = 0;
            while (has && guard++ < MaxTraversal)
            {
                if (Matches(it, key)) return it.Copy();
                has = it.NextVisible(true);
            }
            return null;
        }

        static bool Matches(SerializedProperty p, string key)
        {
            if (string.Equals(p.propertyPath, key, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(p.name, key, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(p.displayName, key, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.propertyPath.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void SetValue(SerializedProperty p, object value)
        {
            if (p.propertyType == SerializedPropertyType.Generic && p.hasVisibleChildren)
            {
                Dictionary<string, object> nested = value as Dictionary<string, object>;
                if (nested == null && value is System.Collections.IDictionary raw)
                {
                    nested = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (System.Collections.DictionaryEntry e in raw) nested[Convert.ToString(e.Key, CultureInfo.InvariantCulture)] = e.Value;
                }
                if (nested == null)
                    throw new BridgeException("invalid_value", "'" + p.propertyPath + "' is a struct; pass an object of its fields.");

                foreach (KeyValuePair<string, object> pair in nested)
                {
                    SerializedProperty child = FindChild(p, pair.Key);
                    if (child == null)
                        throw new BridgeException("unknown_field", "Field '" + pair.Key + "' does not exist under '" + p.propertyPath + "'.");
                    SetValue(child, pair.Value);
                }
                return;
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    p.intValue = (int)Number(value);
                    break;
                case SerializedPropertyType.Boolean:
                    p.boolValue = Boolean(value);
                    break;
                case SerializedPropertyType.Float:
                    p.floatValue = (float)Number(value);
                    break;
                case SerializedPropertyType.String:
                    p.stringValue = value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Enum:
                    p.enumValueIndex = EnumIndex(p, value);
                    break;
                case SerializedPropertyType.LayerMask:
                    p.intValue = (int)Number(value);
                    break;
                case SerializedPropertyType.Vector2:
                    p.vector2Value = Vector2Value(value);
                    break;
                case SerializedPropertyType.Vector3:
                    p.vector3Value = Vector(value);
                    break;
                case SerializedPropertyType.Vector4:
                    p.vector4Value = Vector4(value);
                    break;
                case SerializedPropertyType.Quaternion:
                    p.quaternionValue = QuaternionValue(value);
                    break;
                case SerializedPropertyType.Color:
                    p.colorValue = ColorValue(value);
                    break;
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = ResolveObject(value);
                    break;
                default:
                    throw new BridgeException("unsupported_property",
                        "Writing '" + p.propertyType + "' properties is not supported ('" + p.propertyPath + "').");
            }
        }

        static SerializedProperty FindChild(SerializedProperty parent, string name)
        {
            SerializedProperty p = parent.Copy();
            int parentDepth = parent.depth;
            bool has = p.NextVisible(true);
            int guard = 0;
            while (has && guard++ < MaxTraversal)
            {
                if (p.depth != parentDepth + 1) break;
                if (Matches(p, name)) return p.Copy();
                has = p.NextVisible(false);
            }
            return null;
        }

        static int EnumIndex(SerializedProperty p, object value)
        {
            string[] names = p.enumNames;
            if (value is string text)
            {
                if (names != null)
                {
                    for (int i = 0; i < names.Length; i++)
                        if (string.Equals(names[i], text.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
                }
                int parsed;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
                throw new BridgeException("invalid_value", "Unknown enum value '" + text + "'. Options: " + string.Join("|", names ?? new string[0]));
            }
            return (int)Number(value);
        }

        internal static double Number(object value)
        {
            if (value == null) throw new BridgeException("invalid_value", "Value is null.");
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is bool) return (bool)value ? 1 : 0;
            if (value is string text)
            {
                double parsed;
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
                throw new BridgeException("invalid_value", "'" + text + "' is not a number.");
            }
            throw new BridgeException("invalid_value", "Unsupported numeric value of type " + value.GetType().Name + ".");
        }

        static bool Boolean(object value)
        {
            if (value is bool) return (bool)value;
            if (value == null) return false;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) return false;
            string lowered = text.Trim().ToLowerInvariant();
            if (lowered == "true" || lowered == "1" || lowered == "yes" || lowered == "on") return true;
            if (lowered == "false" || lowered == "0" || lowered == "no" || lowered == "off") return false;
            return Number(value) != 0;
        }

        static List<object> Components(object value, int count, string label)
        {
            var list = new List<object>();
            if (value is System.Collections.IList items)
            {
                foreach (object item in items) list.Add(item);
            }
            else if (value is Dictionary<string, object> dict)
            {
                string[] keys = count == 2 ? new[] { "x", "y" } : count == 3 ? new[] { "x", "y", "z" } : new[] { "x", "y", "z", "w" };
                for (int i = 0; i < keys.Length; i++)
                {
                    object component;
                    if (!dict.TryGetValue(keys[i], out component))
                        throw new BridgeException("invalid_value", "'" + label + "' needs keys x,y" + (count > 2 ? ",z" : string.Empty) + (count > 3 ? ",w" : string.Empty) + ".");
                    list.Add(component);
                }
            }
            else if (value is string text && text.IndexOf(',') >= 0)
            {
                foreach (string part in text.Split(',')) list.Add(part);
            }
            else
            {
                throw new BridgeException("invalid_value", "'" + label + "' needs " + count + " numbers.");
            }
            if (list.Count < count) throw new BridgeException("invalid_value", "'" + label + "' needs " + count + " numbers.");
            return list;
        }

        static Vector2 Vector2Value(object value)
        {
            List<object> c = Components(value, 2, "vector");
            return new Vector2((float)Number(c[0]), (float)Number(c[1]));
        }

        static Vector3 Vector(object value)
        {
            List<object> c = Components(value, 3, "vector");
            return new Vector3((float)Number(c[0]), (float)Number(c[1]), (float)Number(c[2]));
        }

        static Vector4 Vector4(object value)
        {
            List<object> c = Components(value, 4, "vector");
            return new Vector4((float)Number(c[0]), (float)Number(c[1]), (float)Number(c[2]), (float)Number(c[3]));
        }

        static Quaternion QuaternionValue(object value)
        {
            if (value is Dictionary<string, object> dict && dict.ContainsKey("w"))
            {
                List<object> c = Components(value, 4, "quaternion");
                return new Quaternion((float)Number(c[0]), (float)Number(c[1]), (float)Number(c[2]), (float)Number(c[3]));
            }
            Vector3 euler = Vector(value);
            return Quaternion.Euler(euler);
        }

        static Color ColorValue(object value)
        {
            if (value is string text && text.StartsWith("#", StringComparison.Ordinal))
            {
                Color parsed;
                if (ColorUtility.TryParseHtmlString(text, out parsed)) return parsed;
                throw new BridgeException("invalid_value", "'" + text + "' is not a valid colour.");
            }
            List<object> c = Components(value, 4, "color");
            return new Color((float)Number(c[0]), (float)Number(c[1]), (float)Number(c[2]), (float)Number(c[3]));
        }

        internal static UnityEngine.Object ResolveObject(object value)
        {
            if (value == null) return null;
            if (value is UnityEngine.Object direct) return direct;

            if (value is Dictionary<string, object> dict)
            {
                object id;
                if (dict.TryGetValue("instanceId", out id) || dict.TryGetValue("id", out id))
                    return EditorUtility.InstanceIDToObject((int)Number(id));
                object assetPath;
                if (dict.TryGetValue("assetPath", out assetPath) || dict.TryGetValue("path", out assetPath))
                    return ResolveObject(Convert.ToString(assetPath, CultureInfo.InvariantCulture));
                object name;
                if (dict.TryGetValue("name", out name))
                    return ResolveObject(Convert.ToString(name, CultureInfo.InvariantCulture));
                return null;
            }

            if (value is long || value is int) return EditorUtility.InstanceIDToObject((int)Number(value));

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Trim();
            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "null", StringComparison.OrdinalIgnoreCase)) return null;

            if (text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(text);
                if (asset != null) return asset;
                throw new BridgeException("not_found", "No asset at '" + text + "'.");
            }

            GameObject go = SceneSearch.FindByPath(text) ?? SceneSearch.FindByName(text, true);
            if (go != null) return go;

            string[] guids = AssetDatabase.FindAssets(System.IO.Path.GetFileNameWithoutExtension(text));
            for (int i = 0; i < guids.Length && i < 8; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null) return asset;
            }
            throw new BridgeException("not_found", "Nothing in the project matches '" + text + "'.");
        }
    }
}
