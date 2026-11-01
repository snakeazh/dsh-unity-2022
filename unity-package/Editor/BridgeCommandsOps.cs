using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dsh.UnityBridge
{
    /// <summary>Commands that change the scene: objects, components, transforms, scenes.</summary>
    internal static partial class BridgeCommands
    {
        internal static void MarkSceneDirty(GameObject go)
        {
            if (go == null) return;
            Scene scene = go.scene;
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }

        static void TrackModification(UnityEngine.Object target)
        {
            if (target == null) return;
            EditorUtility.SetDirty(target);

            GameObject go = target as GameObject;
            if (go == null)
            {
                Component component = target as Component;
                if (component != null) go = component.gameObject;
            }
            if (go == null) return;

            MarkSceneDirty(go);
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        static bool TryPrimitive(string text, out PrimitiveType type)
        {
            type = PrimitiveType.Cube;
            if (string.IsNullOrEmpty(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "empty":
                case "gameobject":
                case "none":
                    return false;
                case "cube": type = PrimitiveType.Cube; return true;
                case "sphere": type = PrimitiveType.Sphere; return true;
                case "capsule": type = PrimitiveType.Capsule; return true;
                case "cylinder": type = PrimitiveType.Cylinder; return true;
                case "plane": type = PrimitiveType.Plane; return true;
                case "quad": type = PrimitiveType.Quad; return true;
                default:
                    throw new BridgeException("invalid_primitive",
                        "Unknown primitive '" + text + "'. Use Empty, Cube, Sphere, Capsule, Cylinder, Plane or Quad.");
            }
        }

        static GameObject ResolveTarget(Args a)
        {
            GameObject go = SceneSearch.Resolve(a);
            if (go == null)
                throw new BridgeException("not_found", "No GameObject matches the given target.");
            return go;
        }

        // ------------------------------------------------------------- create/delete

        static object CmdCreateObject(Args a)
        {
            string name = a.Str("name", "GameObject");
            string primitive = a.Str("primitive", "Empty");

            GameObject parent = null;
            string parentSpec = a.Str("parent", null);
            if (!string.IsNullOrEmpty(parentSpec))
            {
                parent = SceneSearch.FindByPath(parentSpec) ?? SceneSearch.FindByName(parentSpec, true);
                if (parent == null)
                    throw new BridgeException("not_found", "Parent GameObject '" + parentSpec + "' was not found.");
            }

            PrimitiveType primitiveType;
            bool isPrimitive = TryPrimitive(primitive, out primitiveType);

            GameObject go = isPrimitive ? GameObject.CreatePrimitive(primitiveType) : new GameObject(string.IsNullOrEmpty(name) ? "GameObject" : name);
            if (isPrimitive) go.name = string.IsNullOrEmpty(name) ? primitiveType.ToString() : name;

            if (!a.Bool("collider", true))
            {
                Collider collider = go.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            }

            if (parent != null) go.transform.SetParent(parent.transform, true);

            string space = a.Str("space", "local");
            bool local = !string.Equals(space, "world", StringComparison.OrdinalIgnoreCase);
            Vector3? position = a.Vec3("position");
            if (position.HasValue) { if (local) go.transform.localPosition = position.Value; else go.transform.position = position.Value; }
            Vector3? euler = a.Vec3("euler");
            if (euler.HasValue) { if (local) go.transform.localEulerAngles = euler.Value; else go.transform.eulerAngles = euler.Value; }
            Vector3? scale = a.Vec3("scale") ?? a.Vec3("localScale");
            if (scale.HasValue) go.transform.localScale = scale.Value;

            string tagName = a.Str("tag", null);
            if (!string.IsNullOrEmpty(tagName))
            {
                try { go.tag = tagName; }
                catch (Exception) { throw new BridgeException("invalid_tag", "Tag '" + tagName + "' is not defined in the project."); }
            }
            string layerName = a.Str("layer", null);
            if (!string.IsNullOrEmpty(layerName))
            {
                int layer = LayerMask.NameToLayer(layerName);
                if (layer < 0) throw new BridgeException("invalid_layer", "Layer '" + layerName + "' does not exist.");
                go.layer = layer;
            }
            bool active = a.Bool("active", true);
            go.SetActive(active);

            Undo.RegisterCreatedObjectUndo(go, "DSH Create GameObject");
            MarkSceneDirty(go);
            Selection.activeGameObject = go;

            var result = DescribeObject(go, true);
            result["transform"] = TransformInfo(go.transform);
            result["primitive"] = isPrimitive ? primitiveType.ToString() : null;
            return result;
        }

        static object CmdDeleteObject(Args a)
        {
            GameObject go = ResolveTarget(a);
            string path = SceneSearch.PathOf(go);
            int instanceId = go.GetInstanceID();
            string sceneName = go.scene.IsValid() ? go.scene.name : null;

            Undo.DestroyObjectImmediate(go);
            Scene scene = SceneManager.GetSceneByName(sceneName ?? string.Empty);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);

            var result = D();
            result["deleted"] = path;
            result["instanceId"] = instanceId;
            result["scene"] = sceneName;
            return result;
        }

        // ------------------------------------------------------------------ components

        static bool HasAttribute<T>(Type type) where T : Attribute
        {
            return type.GetCustomAttributes(typeof(T), true).Length > 0;
        }

        static object CmdAddComponent(Args a)
        {
            GameObject go = ResolveTarget(a);
            string typeName = a.RequireStr("type");

            Type type = ResolveType(typeName);
            if (type == null)
                throw new BridgeException("unknown_type", "No component type named '" + typeName + "' was found in the loaded assemblies.");
            if (!typeof(Component).IsAssignableFrom(type))
                throw new BridgeException("invalid_type", "'" + typeName + "' is not a Component.");
            if (type == typeof(Transform) || type == typeof(RectTransform))
                throw new BridgeException("invalid_type", "'" + typeName + "' cannot be added; every GameObject already has one.");

            var result = D();
            result["type"] = type.Name;
            result["gameObject"] = SceneSearch.PathOf(go);

            Component existing = go.GetComponent(type);
            if (existing != null && HasAttribute<DisallowMultipleComponent>(type))
            {
                result["alreadyPresent"] = true;
                result["instanceId"] = existing.GetInstanceID();
                result["index"] = Array.IndexOf(go.GetComponents<Component>(), existing);
                return result;
            }

            Component component = Undo.AddComponent(go, type);
            if (component == null)
                throw new BridgeException("add_failed", "Unity refused to add '" + typeName + "' to '" + SceneSearch.PathOf(go) + "'.");

            var behaviour = component as Behaviour;
            if (behaviour != null && a.Has("enabled")) behaviour.enabled = a.Bool("enabled", true);
            var renderer = component as Renderer;
            if (renderer != null && a.Has("enabled")) renderer.enabled = a.Bool("enabled", true);

            Dictionary<string, object> properties = a.Dict("properties");
            if (properties.Count > 0)
            {
                Undo.RecordObject(component, "DSH Add Component");
                int applied;
                List<object> propertyResults = Serialized.ApplyProperties(component, properties, out applied);
                result["applied"] = applied;
                result["properties"] = propertyResults;
            }

            TrackModification(component);
            result["instanceId"] = component.GetInstanceID();
            result["index"] = Array.IndexOf(go.GetComponents<Component>(), component);
            return result;
        }

        static object CmdRemoveComponent(Args a)
        {
            GameObject go = ResolveTarget(a);
            Component component = SceneSearch.ResolveComponent(a, go, "type");
            if (component == null)
                throw new BridgeException("not_found", "The GameObject has no component matching '" + a.Str("type", "?") + "'.");
            if (component is Transform)
                throw new BridgeException("invalid_target", "Transform cannot be removed.");

            string typeName = component.GetType().Name;
            int instanceId = component.GetInstanceID();

            Undo.DestroyObjectImmediate(component);
            MarkSceneDirty(go);

            var result = D();
            result["removed"] = typeName;
            result["instanceId"] = instanceId;
            result["gameObject"] = SceneSearch.PathOf(go);
            result["components"] = ComponentNames(go);
            return result;
        }

        static object CmdSetProperty(Args a)
        {
            GameObject go = ResolveTarget(a);
            Component component = SceneSearch.ResolveComponent(a, go, "type");
            if (component == null)
                throw new BridgeException("not_found", "The GameObject has no component matching '" + a.Str("type", "?") + "'.");

            Dictionary<string, object> properties = a.Dict("properties");
            if (properties.Count == 0)
                throw new BridgeException("missing_param", "Provide 'properties' as an object of field names to values.");

            Undo.RecordObject(component, "DSH Set Property");
            int applied;
            List<object> results = Serialized.ApplyProperties(component, properties, out applied);
            TrackModification(component);

            var result = D();
            result["gameObject"] = SceneSearch.PathOf(go);
            result["component"] = component.GetType().Name;
            result["applied"] = applied;
            result["requested"] = properties.Count;
            result["results"] = results;
            return result;
        }

        // --------------------------------------------------------------------- transform

        static object CmdSetTransform(Args a)
        {
            GameObject go = ResolveTarget(a);
            Transform t = go.transform;
            Undo.RecordObject(t, "DSH Set Transform");

            string space = a.Str("space", "world");
            bool world = !string.Equals(space, "local", StringComparison.OrdinalIgnoreCase);

            Vector3? position = a.Vec3("position");
            if (position.HasValue)
            {
                if (world) t.position = position.Value;
                else t.localPosition = position.Value;
            }

            Vector3? localPosition = a.Vec3("localPosition");
            if (localPosition.HasValue) t.localPosition = localPosition.Value;

            Quaternion? rotation = a.Quat("rotation");
            if (rotation.HasValue)
            {
                if (world) t.rotation = rotation.Value;
                else t.localRotation = rotation.Value;
            }

            Vector3? euler = a.Vec3("euler") ?? a.Vec3("eulerAngles");
            if (euler.HasValue)
            {
                if (world) t.eulerAngles = euler.Value;
                else t.localEulerAngles = euler.Value;
            }

            Vector3? localEuler = a.Vec3("localEulerAngles");
            if (localEuler.HasValue) t.localEulerAngles = localEuler.Value;

            Vector3? scale = a.Vec3("localScale") ?? a.Vec3("scale");
            if (scale.HasValue) t.localScale = scale.Value;

            string parentSpec = a.Str("parent", null);
            if (parentSpec != null)
            {
                if (parentSpec.Length == 0 || string.Equals(parentSpec, "none", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parentSpec, "root", StringComparison.OrdinalIgnoreCase))
                {
                    t.SetParent(null, world);
                }
                else
                {
                    GameObject parent = SceneSearch.FindByPath(parentSpec) ?? SceneSearch.FindByName(parentSpec, true);
                    if (parent == null) throw new BridgeException("not_found", "Parent '" + parentSpec + "' was not found.");
                    if (parent == go) throw new BridgeException("invalid_target", "An object cannot be its own parent.");
                    if (parent.transform.IsChildOf(t)) throw new BridgeException("invalid_target", "Cannot parent '" + go.name + "' under its own descendant.");
                    t.SetParent(parent.transform, world);
                }
            }

            TrackModification(t);
            var result = D();
            result["gameObject"] = SceneSearch.PathOf(go);
            result["transform"] = TransformInfo(t);
            return result;
        }

        // ------------------------------------------------------------------------ scenes

        static object CmdSaveScene(Args a)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) throw new BridgeException("no_scene", "There is no active scene to save.");

            string path = a.Str("path", null);
            bool saved;
            if (string.IsNullOrEmpty(path))
            {
                // Saving an untitled scene without a path makes the editor open a modal "Save
                // Scene" dialog, which freezes the main thread until a human clicks it.
                if (string.IsNullOrEmpty(scene.path))
                    throw new BridgeException("missing_param",
                        "The open scene has no asset path yet; pass 'path' (for example 'Assets/Scenes/Main.unity') to save it as a new scene.");
                saved = EditorSceneManager.SaveScene(scene);
            }
            else
            {
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("invalid_path", "Scene path must start with 'Assets/', got '" + path + "'.");

                // SaveScene does not create folders, and a folder made behind the AssetDatabase's
                // back makes the editor fall back to a modal dialog: create it as an asset.
                EnsureAssetFolder(path);
                saved = EditorSceneManager.SaveScene(scene, path, false);
                if (!saved)
                    throw new BridgeException("save_failed", "The editor could not write '" + path + "'. Check that the folder is a valid asset folder.");
                scene = SceneManager.GetActiveScene();
            }
            AssetDatabase.Refresh();

            var result = D();
            result["saved"] = saved;
            result["scene"] = SceneInfo(scene);
            return result;
        }

        /// <summary>Creates every missing folder of an asset path ("Assets/A/B/C.unity").</summary>
        static void EnsureAssetFolder(string assetPath)
        {
            string folder = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(folder)) return;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static object CmdOpenScene(Args a)
        {
            string path = a.RequireStr("path");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("invalid_path", "Scene path must start with 'Assets/', got '" + path + "'.");
            SceneAsset asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (asset == null) throw new BridgeException("not_found", "No scene asset at '" + path + "'.");

            bool saveCurrent = a.Bool("saveCurrent", true);
            if (saveCurrent)
            {
                Scene active = SceneManager.GetActiveScene();
                if (active.IsValid() && active.isDirty) EditorSceneManager.SaveOpenScenes();
            }

            bool additive = a.Bool("additive", false);
            Scene opened = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);

            var result = D();
            result["scene"] = SceneInfo(opened);
            result["additive"] = additive;
            result["openScenes"] = SceneManager.sceneCount;
            return result;
        }

        static object CmdSaveAssets(Args a)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var result = D();
            result["saved"] = true;
            result["activeScene"] = SceneInfo(SceneManager.GetActiveScene());
            return result;
        }

        static object CmdRefreshAssets(Args a)
        {
            AssetDatabase.Refresh(a.Bool("force", false) ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
            var result = D();
            result["refreshed"] = true;
            result["isCompiling"] = EditorApplication.isCompiling;
            return result;
        }

        // ------------------------------------------------------------------- selection

        static List<object> DescribeSelection()
        {
            var list = new List<object>();
            UnityEngine.Object[] objects = Selection.objects;
            for (int i = 0; i < objects.Length; i++)
            {
                UnityEngine.Object o = objects[i];
                if (o == null) continue;
                var d = D();
                d["name"] = o.name;
                d["type"] = o.GetType().Name;
                d["instanceId"] = o.GetInstanceID();
                GameObject go = o as GameObject;
                if (go != null) d["path"] = SceneSearch.PathOf(go);
                else
                {
                    Component component = o as Component;
                    if (component != null && component.gameObject != null)
                        d["path"] = SceneSearch.PathOf(component.gameObject) + ":" + component.GetType().Name;
                }
                list.Add(d);
            }
            return list;
        }

        static object CmdSelection(Args a)
        {
            var paths = a.Arr("paths");
            var ids = a.Arr("targetIds") ?? a.Arr("instanceIds");
            var requested = new List<UnityEngine.Object>();
            var missing = new List<object>();

            if (paths != null)
            {
                foreach (object item in paths)
                {
                    string path = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(path)) continue;
                    GameObject go = SceneSearch.FindByPath(path) ?? SceneSearch.FindByName(path, true);
                    if (go == null) missing.Add(path);
                    else requested.Add(go);
                }
            }
            if (ids != null)
            {
                foreach (object item in ids)
                {
                    UnityEngine.Object o = EditorUtility.InstanceIDToObject((int)Args.AsDouble(item));
                    if (o == null) missing.Add(Args.AsDouble(item));
                    else requested.Add(o);
                }
            }

            if (requested.Count > 0 || missing.Count > 0 || a.Has("paths") || a.Has("targetIds"))
                Selection.objects = requested.ToArray();

            var result = D();
            result["selection"] = DescribeSelection();
            if (missing.Count > 0) result["missing"] = missing;
            return result;
        }

        static object CmdUndo(Args a)
        {
            string action = a.Str("action", "undo").Trim().ToLowerInvariant();
            string group = Undo.GetCurrentGroupName();
            if (action == "redo") Undo.PerformRedo();
            else if (action == "undo") Undo.PerformUndo();
            else throw new BridgeException("invalid_param", "Unknown undo action '" + action + "'. Use undo or redo.");

            var result = D();
            result["action"] = action;
            result["previousGroup"] = group;
            result["nextGroup"] = Undo.GetCurrentGroupName();
            return result;
        }

        static object CmdExecuteMenu(Args a)
        {
            string path = a.RequireStr("path");
            bool executed = EditorApplication.ExecuteMenuItem(path);
            var result = D();
            result["path"] = path;
            result["executed"] = executed;
            if (!executed) result["hint"] = "Unity could not run that menu item; check the exact path shown in the editor menu.";
            return result;
        }
    }
}
