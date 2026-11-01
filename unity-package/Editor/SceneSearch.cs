using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dsh.UnityBridge
{
    /// <summary>Hierarchy path building and GameObject/Component lookup across all loaded scenes.</summary>
    internal static class SceneSearch
    {
        public const int MaxScanObjects = 200000;

        public static string PathOf(GameObject go)
        {
            if (go == null) return null;
            var parts = new List<string>(8);
            Transform t = go.transform;
            int guard = 0;
            while (t != null && guard++ < 512)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        public static bool IsInScene(GameObject go)
        {
            return go != null && go.scene.IsValid() && go.scene.isLoaded;
        }

        public static List<GameObject> All(bool includeInactive)
        {
            var result = new List<GameObject>();
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                GameObject[] roots;
                try { roots = scene.GetRootGameObjects(); }
                catch (Exception) { continue; }
                for (int r = 0; r < roots.Length; r++)
                    Collect(roots[r], includeInactive, result);
                if (result.Count > MaxScanObjects) break;
            }
            return result;
        }

        static void Collect(GameObject go, bool includeInactive, List<GameObject> into)
        {
            if (go == null) return;
            if (!includeInactive && !go.activeInHierarchy) return;
            into.Add(go);
            Transform t = go.transform;
            int childCount = t.childCount;
            for (int i = 0; i < childCount; i++)
                Collect(t.GetChild(i).gameObject, includeInactive, into);
        }

        public static GameObject FindByInstanceId(int instanceId)
        {
            try
            {
                GameObject go = UnityEditor.EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (go != null) return go;
                Component component = UnityEditor.EditorUtility.InstanceIDToObject(instanceId) as Component;
                if (component != null) return component.gameObject;
            }
            catch (Exception) { }
            return null;
        }

        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                GameObject direct = GameObject.Find(path);
                if (direct != null) return direct;
            }
            catch (Exception) { }

            string[] segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            List<GameObject> all = All(true);
            for (int i = 0; i < all.Count; i++)
            {
                GameObject candidate = all[i];
                if (candidate == null) continue;
                if (candidate.name == segments[0] && candidate.transform.parent == null)
                {
                    GameObject resolved = Descend(candidate.transform, segments, 1);
                    if (resolved != null) return resolved;
                }
            }
            for (int i = 0; i < all.Count; i++)
            {
                GameObject candidate = all[i];
                if (candidate == null) continue;
                string candidatePath = PathOf(candidate);
                if (string.Equals(candidatePath, path, StringComparison.OrdinalIgnoreCase)) return candidate;
                if (candidatePath.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            return null;
        }

        static GameObject Descend(Transform node, string[] segments, int index)
        {
            if (index >= segments.Length) return node.gameObject;
            for (int i = 0; i < node.childCount; i++)
            {
                Transform child = node.GetChild(i);
                if (child != null && string.Equals(child.name, segments[index], StringComparison.OrdinalIgnoreCase))
                {
                    GameObject found = Descend(child, segments, index + 1);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public static GameObject FindByName(string name, bool includeInactive)
        {
            if (string.IsNullOrEmpty(name)) return null;
            List<GameObject> all = All(includeInactive);
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && string.Equals(all[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return all[i];
            return null;
        }

        public static GameObject Resolve(Args args, string key = "target")
        {
            int? id = OptionalInt(args, key + "Id");
            if (!id.HasValue && (key == "target")) id = OptionalInt(args, "instanceId");
            if (id.HasValue)
            {
                GameObject byId = FindByInstanceId(id.Value);
                if (byId == null)
                    throw new BridgeException("not_found", "No GameObject with instance id " + id.Value + " in loaded scenes.");
                return byId;
            }

            string target = args.Str(key, null);
            if (string.IsNullOrEmpty(target)) target = args.Str("path", null);
            if (string.IsNullOrEmpty(target)) target = args.Str("name", null);
            if (string.IsNullOrEmpty(target))
                throw new BridgeException("missing_param",
                    "Provide '" + key + "' (hierarchy path such as 'Canvas/Button', or a GameObject name) or '" + key + "Id'.");

            if (string.Equals(target, "root", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, "/", StringComparison.Ordinal))
                return null;

            GameObject found = target.IndexOf('/') >= 0 ? FindByPath(target) : FindByName(target, true);
            if (found == null) found = FindByPath(target);
            if (found == null)
                throw new BridgeException("not_found", "No GameObject matches '" + target + "'.");
            return found;
        }

        static int? OptionalInt(Args args, string key)
        {
            object raw = args.Raw(key);
            if (raw == null) return null;
            if (raw is long) return (int)(long)raw;
            if (raw is double) return (int)(double)raw;
            int parsed;
            if (int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return null;
        }

        /// <summary>
        /// Resolves "Canvas/Button", "Canvas/Button:Text", "Canvas/Button:Text:0" or a bare component type on a target object.
        /// </summary>
        public static Component ResolveComponent(Args args, GameObject go, string typeKey = "type")
        {
            string typeName = args.Str(typeKey, null);
            int index = args.Int("index", 0);
            if (!string.IsNullOrEmpty(typeName))
            {
                int colon = typeName.IndexOf(':');
                if (colon > 0)
                {
                    string suffix = typeName.Substring(colon + 1);
                    typeName = typeName.Substring(0, colon);
                    int parsedIndex;
                    if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedIndex)) index = parsedIndex;
                }
            }

            Component[] components = go.GetComponents<Component>();
            if (string.IsNullOrEmpty(typeName))
            {
                if (components.Length == 0)
                    throw new BridgeException("not_found", "'" + go.name + "' has no components.");
                if (index < 0 || index >= components.Length)
                    throw new BridgeException("not_found", "Component index " + index + " out of range (0.." + (components.Length - 1) + ").");
                return components[index];
            }

            var matches = new List<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null) continue;
                Type t = c.GetType();
                if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(c);
            }
            if (matches.Count == 0)
                throw new BridgeException("not_found", "'" + go.name + "' has no component of type '" + typeName + "'. Components: " + ComponentList(components) + ".");
            if (index < 0 || index >= matches.Count)
                throw new BridgeException("not_found", "'" + go.name + "' has " + matches.Count + " component(s) of type '" + typeName + "', index " + index + " is out of range.");
            return matches[index];
        }

        public static string ComponentList(Component[] components)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(components[i].GetType().Name);
            }
            return sb.ToString();
        }
    }
}
