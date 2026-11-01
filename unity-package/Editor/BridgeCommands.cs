using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dsh.UnityBridge
{
    internal sealed class CommandSpec
    {
        internal string Name;
        internal string Summary;
        internal string[] Params;
        internal string Example;
        internal bool Mutating;
        internal Func<Args, object> Handler;
    }

    /// <summary>Registry of editor commands plus the shared helpers they use.</summary>
    internal static partial class BridgeCommands
    {
        internal static readonly Dictionary<string, CommandSpec> Commands = Build();

        public static object Invoke(string name, Args args)
        {
            CommandSpec spec;
            if (string.IsNullOrEmpty(name) || !Commands.TryGetValue(name, out spec))
            {
                throw new BridgeException("unknown_command",
                    "Unknown command '" + name + "'. Available: " + string.Join(", ", Commands.Keys.OrderBy(k => k).ToArray()) + ".");
            }
            return spec.Handler(args ?? Args.Empty);
        }

        static Dictionary<string, CommandSpec> Build()
        {
            var map = new Dictionary<string, CommandSpec>(StringComparer.Ordinal);
            Add(map, "status", "Editor/engine status: version, play mode, compilation, open scenes, selection.", new string[0], null, false, CmdStatus);
            Add(map, "list_commands", "Describe every command this bridge understands.", new string[0], null, false, CmdListCommands);
            Add(map, "hierarchy", "Read the scene hierarchy as a tree.",
                new[] { "root=path", "maxDepth=4", "includeComponents=false", "includeInactive=true", "maxNodes=3000" },
                "{\"maxDepth\":2,\"includeComponents\":true}", false, CmdHierarchy);
            Add(map, "find_objects", "Find GameObjects by name, component type, tag or layer.",
                new[] { "name=substring", "component=Type", "tag=Tag", "layer=Name", "limit=50", "includeInactive=true" },
                "{\"name\":\"Button\"}", false, CmdFindObjects);
            Add(map, "inspect_object", "Read one GameObject: transform, components and serialized property values.",
                new[] { "target=path|name", "targetId=instanceId" }, "{\"target\":\"Canvas/Button\"}", false, CmdInspectObject);
            Add(map, "create_object", "Create a GameObject (optionally a primitive) under a parent.",
                new[] { "name=Name", "parent=path", "primitive=Empty|Cube|Sphere|Capsule|Cylinder|Plane|Quad", "position=[x,y,z]", "euler=[x,y,z]", "scale=[x,y,z]", "tag=Tag", "layer=Name" },
                "{\"name\":\"Crate\",\"primitive\":\"Cube\",\"position\":[0,0.5,0]}", true, CmdCreateObject);
            Add(map, "delete_object", "Delete a GameObject and its children.", new[] { "target=path|name", "targetId=instanceId" }, "{\"target\":\"Crate\"}", true, CmdDeleteObject);
            Add(map, "add_component", "Add a component to a GameObject and optionally set its fields.",
                new[] { "target=path|name", "type=TypeName", "properties={field:value}" },
                "{\"target\":\"Crate\",\"type\":\"Rigidbody\",\"properties\":{\"m_Mass\":2}}", true, CmdAddComponent);
            Add(map, "remove_component", "Remove a component from a GameObject.",
                new[] { "target=path|name", "type=TypeName", "index=0" }, "{\"target\":\"Crate\",\"type\":\"Rigidbody\"}", true, CmdRemoveComponent);
            Add(map, "set_transform", "Set position / rotation / scale.",
                new[] { "target=path|name", "position=[x,y,z]", "euler=[x,y,z]", "localScale=[x,y,z]", "space=world|local" },
                "{\"target\":\"Crate\",\"position\":[1,0,2]}", true, CmdSetTransform);
            Add(map, "set_property", "Set serialized fields on a component.",
                new[] { "target=path|name", "type=TypeName", "properties={field:value}" },
                "{\"target\":\"Directional Light\",\"type\":\"Light\",\"properties\":{\"m_Intensity\":2}}", true, CmdSetProperty);
            Add(map, "save_scene", "Save the active scene (optionally to a new path).",
                new[] { "path=Assets/Scenes/Main.unity" }, null, true, CmdSaveScene);
            Add(map, "open_scene", "Open a scene asset, optionally additive.",
                new[] { "path=Assets/Scenes/Main.unity", "additive=false", "saveCurrent=true" }, "{\"path\":\"Assets/Scenes/Main.unity\"}", true, CmdOpenScene);
            Add(map, "save_assets", "Flush pending asset changes and refresh the AssetDatabase.", new string[0], null, true, CmdSaveAssets);
            Add(map, "refresh_assets", "Rescan the AssetDatabase for changed files.", new string[0], null, true, CmdRefreshAssets);
            Add(map, "write_file", "Write a text file inside the project and refresh assets.",
                new[] { "path=relative/path.cs", "content=text", "append=false", "refresh=true" }, "{\"path\":\"Assets/Scripts/Foo.cs\",\"content\":\"...\"}", true, CmdWriteFile);
            Add(map, "import_package", "Import a .unitypackage into the project.",
                new[] { "path=absolute-or-relative.unitypackage" }, null, true, CmdImportPackage);
            Add(map, "execute_menu", "Run an editor menu item by its path.",
                new[] { "path=Window/General/Console" }, "{\"path\":\"Edit/Play\"}", true, CmdExecuteMenu);
            Add(map, "selection", "Read or replace the current editor selection.",
                new[] { "paths=[path,...]", "targetIds=[instanceId,...]" }, "{\"paths\":[\"Main Camera\"]}", true, CmdSelection);
            Add(map, "undo", "Undo or redo one step.", new[] { "action=undo|redo" }, null, true, CmdUndo);
            Add(map, "play", "Enter, stop, pause or single-step play mode.",
                new[] { "action=play|stop|pause|resume|step" }, "{\"action\":\"play\"}", true, CmdPlay);
            Add(map, "screenshot", "Capture the scene view or game view to a PNG file.",
                new[] { "view=scene|game", "width=1280", "height=720", "path=absolute.png" },
                "{\"view\":\"scene\",\"width\":1600,\"height\":900}", false, CmdScreenshot);
            Add(map, "get_logs", "Read buffered console output.",
                new[] { "since=index", "level=all|log|warning|error", "limit=200", "stackTrace=false" }, null, false, CmdGetLogs);
            Add(map, "clear_logs", "Drop the buffered console output.", new string[0], null, true, CmdClearLogs);
            Add(map, "eval", "Compile and run a C# snippet inside the editor, returning its value.",
                new[] { "code=C#", "mode=auto|expression|statement" },
                "{\"code\":\"return Application.unityVersion;\"}", true, CmdEval);
            Add(map, "build", "Run a player build for the active build target list.",
                new[] { "target=StandaloneWindows64", "output=Builds/Windows/game.exe", "scenes=[Assets/...]" }, null, true, CmdBuild);
            return map;
        }

        static void Add(Dictionary<string, CommandSpec> map, string name, string summary, string[] parameters, string example,
                        bool mutating, Func<Args, object> handler)
        {
            var spec = new CommandSpec();
            spec.Name = name;
            spec.Summary = summary;
            spec.Params = parameters ?? new string[0];
            spec.Example = example;
            spec.Mutating = mutating;
            spec.Handler = handler;
            map[name] = spec;
        }

        // ---------------------------------------------------------------- helpers

        internal static Dictionary<string, object> D()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        static Dictionary<string, object> Vec(Vector3 v)
        {
            var d = D();
            d["x"] = (double)v.x;
            d["y"] = (double)v.y;
            d["z"] = (double)v.z;
            return d;
        }

        internal static Dictionary<string, object> SceneInfo(Scene scene)
        {
            var d = D();
            d["name"] = scene.name;
            d["path"] = scene.path;
            d["isDirty"] = scene.isDirty;
            d["isLoaded"] = scene.isLoaded;
            d["rootCount"] = scene.rootCount;
            d["buildIndex"] = scene.buildIndex;
            return d;
        }

        internal static List<object> ComponentNames(GameObject go)
        {
            var names = new List<object>();
            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                names.Add(components[i].GetType().Name);
            }
            return names;
        }

        internal static Dictionary<string, object> DescribeObject(GameObject go, bool withComponents)
        {
            var d = D();
            d["name"] = go.name;
            d["path"] = SceneSearch.PathOf(go);
            d["instanceId"] = go.GetInstanceID();
            d["activeSelf"] = go.activeSelf;
            d["activeInHierarchy"] = go.activeInHierarchy;
            d["tag"] = go.tag;
            d["layer"] = LayerMask.LayerToName(go.layer);
            d["scene"] = go.scene.IsValid() ? go.scene.name : null;
            d["childCount"] = go.transform.childCount;
            if (withComponents) d["components"] = ComponentNames(go);
            return d;
        }

        internal static Dictionary<string, object> TransformInfo(Transform t)
        {
            var d = D();
            d["position"] = Vec(t.position);
            d["localPosition"] = Vec(t.localPosition);
            d["eulerAngles"] = Vec(t.eulerAngles);
            d["localEulerAngles"] = Vec(t.localEulerAngles);
            d["localScale"] = Vec(t.localScale);
            d["parent"] = t.parent == null ? null : SceneSearch.PathOf(t.parent.gameObject);
            return d;
        }

        internal static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            Type direct = Type.GetType(name, false);
            if (direct != null) return direct;

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Transform", "UnityEngine.Transform" },
                { "Rigidbody", "UnityEngine.Rigidbody" },
                { "BoxCollider", "UnityEngine.BoxCollider" },
                { "MeshRenderer", "UnityEngine.MeshRenderer" },
                { "MeshFilter", "UnityEngine.MeshFilter" },
                { "Camera", "UnityEngine.Camera" },
                { "Light", "UnityEngine.Light" },
                { "AudioSource", "UnityEngine.AudioSource" },
                { "Animator", "UnityEngine.Animator" },
                { "Canvas", "UnityEngine.Canvas" },
                { "Text", "UnityEngine.UI.Text" },
                { "Image", "UnityEngine.UI.Image" },
                { "Button", "UnityEngine.UI.Button" }
            };
            string alias;
            if (aliases.TryGetValue(name, out alias))
            {
                Type aliased = Type.GetType(alias + ", UnityEngine.CoreModule", false) ?? Type.GetType(alias, false);
                if (aliased != null) return aliased;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types = SafeTypes(assemblies[i]);
                for (int t = 0; t < types.Length; t++)
                {
                    Type candidate = types[t];
                    if (candidate == null) continue;
                    if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) return candidate;
                }
            }
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types = SafeTypes(assemblies[i]);
                for (int t = 0; t < types.Length; t++)
                {
                    Type candidate = types[t];
                    if (candidate == null) continue;
                    if (string.Equals(candidate.FullName, name, StringComparison.OrdinalIgnoreCase)) return candidate;
                }
            }
            return null;
        }

        internal static Type[] SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
            catch (Exception) { return new Type[0]; }
        }

        // --------------------------------------------------------------- commands

        static object CmdStatus(Args a)
        {
            var d = D();
            d["bridgeVersion"] = BridgeDiscovery.BridgeVersion;
            d["protocol"] = BridgeDiscovery.ProtocolVersion;
            d["unityVersion"] = Application.unityVersion;
            d["isRequiredUnityVersion"] = string.Equals(Application.unityVersion, "2022.3.62f3", StringComparison.Ordinal);
            d["projectPath"] = BridgeDiscovery.ProjectRoot;
            d["projectName"] = new DirectoryInfo(BridgeDiscovery.ProjectRoot).Name;
            d["productName"] = Application.productName;
            d["companyName"] = Application.companyName;
            d["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            d["port"] = BridgeServer.Port;
            d["isPlaying"] = EditorApplication.isPlaying;
            d["isPaused"] = EditorApplication.isPaused;
            d["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode;
            d["isCompiling"] = EditorApplication.isCompiling;
            d["isUpdating"] = EditorApplication.isUpdating;
            d["scriptCompilationFailed"] = EditorUtility.scriptCompilationFailed;
            d["isFocused"] = Application.isFocused;
            d["platform"] = Application.platform.ToString();
            d["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString();
            d["frameCount"] = Time.frameCount;
            d["timeSinceStartup"] = (double)Time.realtimeSinceStartup;
            d["requestsServed"] = BridgeServer.Requests;
            d["lastCommand"] = BridgeServer.LastCommand;

            Scene active = SceneManager.GetActiveScene();
            d["activeScene"] = active.IsValid() ? SceneInfo(active) : null;

            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid()) scenes.Add(SceneInfo(scene));
            }
            d["openScenes"] = scenes;

            var selection = new List<object>();
            GameObject[] selected = Selection.gameObjects;
            for (int i = 0; i < selected.Length; i++)
                if (selected[i] != null) selection.Add(SceneSearch.PathOf(selected[i]));
            d["selection"] = selection;

            return d;
        }

        static object CmdListCommands(Args a)
        {
            var list = new List<object>();
            foreach (CommandSpec spec in Commands.Values.OrderBy(s => s.Name))
            {
                var d = D();
                d["name"] = spec.Name;
                d["summary"] = spec.Summary;
                d["params"] = new List<object>(spec.Params.Cast<object>());
                d["mutating"] = spec.Mutating;
                if (!string.IsNullOrEmpty(spec.Example)) d["example"] = spec.Example;
                list.Add(d);
            }
            var result = D();
            result["count"] = list.Count;
            result["commands"] = list;
            return result;
        }

        static object CmdHierarchy(Args a)
        {
            int maxDepth = Math.Max(0, a.Int("maxDepth", 4));
            bool withComponents = a.Bool("includeComponents", false);
            bool includeInactive = a.Bool("includeInactive", true);
            int maxNodes = Math.Max(1, a.Int("maxNodes", 3000));

            var counter = new int[1];
            var roots = new List<object>();

            string rootSpec = a.Str("root", null);
            GameObject root = null;
            if (!string.IsNullOrEmpty(rootSpec) && !string.Equals(rootSpec, "root", StringComparison.OrdinalIgnoreCase))
            {
                root = SceneSearch.FindByPath(rootSpec) ?? SceneSearch.FindByName(rootSpec, true);
                if (root == null) throw new BridgeException("not_found", "No GameObject matches '" + rootSpec + "'.");
            }

            if (root != null)
            {
                Dictionary<string, object> node = HierarchyNode(root, 0, maxDepth, withComponents, includeInactive, maxNodes, counter);
                if (node != null) roots.Add(node);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.IsValid() || !scene.isLoaded) continue;
                    GameObject[] sceneRoots = scene.GetRootGameObjects();
                    for (int r = 0; r < sceneRoots.Length; r++)
                    {
                        Dictionary<string, object> node = HierarchyNode(sceneRoots[r], 0, maxDepth, withComponents, includeInactive, maxNodes, counter);
                        if (node != null) roots.Add(node);
                        if (counter[0] >= maxNodes) break;
                    }
                    if (counter[0] >= maxNodes) break;
                }
            }

            var result = D();
            result["roots"] = roots;
            result["nodeCount"] = counter[0];
            result["truncated"] = counter[0] >= maxNodes;
            result["maxDepth"] = maxDepth;
            return result;
        }

        static Dictionary<string, object> HierarchyNode(GameObject go, int depth, int maxDepth, bool withComponents,
                                                       bool includeInactive, int maxNodes, int[] counter)
        {
            if (go == null) return null;
            if (!includeInactive && !go.activeInHierarchy) return null;
            if (counter[0] >= maxNodes) return null;
            counter[0]++;

            var d = D();
            d["name"] = go.name;
            d["path"] = SceneSearch.PathOf(go);
            d["instanceId"] = go.GetInstanceID();
            if (!go.activeSelf) d["active"] = false;
            if (go.tag != "Untagged") d["tag"] = go.tag;
            if (go.layer != 0) d["layer"] = LayerMask.LayerToName(go.layer);
            if (withComponents) d["components"] = ComponentNames(go);

            Transform t = go.transform;
            if (depth < maxDepth && t.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < t.childCount; i++)
                {
                    Dictionary<string, object> child = HierarchyNode(t.GetChild(i).gameObject, depth + 1, maxDepth, withComponents, includeInactive, maxNodes, counter);
                    if (child != null) children.Add(child);
                    if (counter[0] >= maxNodes) break;
                }
                d["children"] = children;
            }
            else if (t.childCount > 0)
            {
                d["childCount"] = t.childCount;
            }
            return d;
        }

        static object CmdFindObjects(Args a)
        {
            string name = a.Str("name", null);
            string componentName = a.Str("component", null);
            string tag = a.Str("tag", null);
            string layerName = a.Str("layer", null);
            string pathFilter = a.Str("path", null);
            int limit = Math.Max(1, a.Int("limit", 50));
            bool includeInactive = a.Bool("includeInactive", true);

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(componentName) && string.IsNullOrEmpty(tag) &&
                string.IsNullOrEmpty(layerName) && string.IsNullOrEmpty(pathFilter))
                throw new BridgeException("missing_param", "Provide at least one of: name, component, tag, layer, path.");

            Type componentType = string.IsNullOrEmpty(componentName) ? null : ResolveType(componentName);
            int layer = -1;
            if (!string.IsNullOrEmpty(layerName))
            {
                layer = LayerMask.NameToLayer(layerName);
                int parsed;
                if (layer < 0 && int.TryParse(layerName, out parsed)) layer = parsed;
                if (layer < 0) throw new BridgeException("invalid_param", "Unknown layer '" + layerName + "'.");
            }

            List<GameObject> all = SceneSearch.All(includeInactive);
            var matches = new List<object>();
            int total = 0;

            for (int i = 0; i < all.Count; i++)
            {
                GameObject go = all[i];
                if (go == null) continue;
                if (!string.IsNullOrEmpty(name) && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(tag) && !string.Equals(go.tag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                if (layer >= 0 && go.layer != layer) continue;
                if (!string.IsNullOrEmpty(pathFilter) &&
                    SceneSearch.PathOf(go).IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(componentName))
                {
                    bool has = componentType != null ? go.GetComponent(componentType) != null : HasComponentByName(go, componentName);
                    if (!has) continue;
                }

                total++;
                if (matches.Count >= limit) continue;
                Dictionary<string, object> d = DescribeObject(go, true);
                matches.Add(d);
            }

            var result = D();
            result["count"] = total;
            result["returned"] = matches.Count;
            result["truncated"] = total > matches.Count;
            if (componentType == null && !string.IsNullOrEmpty(componentName))
                result["componentTypeResolved"] = false;
            result["objects"] = matches;
            return result;
        }

        static bool HasComponentByName(GameObject go, string typeName)
        {
            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                if (string.Equals(components[i].GetType().Name, typeName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static object CmdInspectObject(Args a)
        {
            GameObject go = SceneSearch.Resolve(a);
            if (go == null) throw new BridgeException("not_found", "Target object is null.");

            Dictionary<string, object> d = DescribeObject(go, true);
            d["transform"] = TransformInfo(go.transform);
            d["isActiveInHierarchy"] = go.activeInHierarchy;
            d["hideFlags"] = go.hideFlags.ToString();

            string componentFilter = a.Str("component", null);
            bool withProperties = a.Bool("properties", true);
            int budget = Math.Max(1, a.Int("maxProperties", 120));

            var components = new List<object>();
            Component[] all = go.GetComponents<Component>();
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null)
                {
                    var missing = D();
                    missing["type"] = "<missing script>";
                    missing["index"] = i;
                    components.Add(missing);
                    continue;
                }
                if (!string.IsNullOrEmpty(componentFilter) &&
                    !string.Equals(c.GetType().Name, componentFilter, StringComparison.OrdinalIgnoreCase)) continue;

                Dictionary<string, object> info = D();
                info["index"] = i;
                info["type"] = c.GetType().Name;
                info["fullType"] = c.GetType().FullName;
                info["instanceId"] = c.GetInstanceID();
                var behaviour = c as Behaviour;
                if (behaviour != null) info["enabled"] = behaviour.enabled;
                var renderer = c as Renderer;
                if (renderer != null) info["enabled"] = renderer.enabled;

                if (withProperties && budget > 0)
                {
                    List<object> properties = Serialized.ReadProperties(c, budget);
                    budget -= properties.Count;
                    if (properties.Count > 0) info["properties"] = properties;
                }
                components.Add(info);
            }
            d["components"] = components;
            return d;
        }

        /// <summary>Writes every registered command to the console, with its parameters.</summary>
        internal static void LogAll()
        {
            var names = new List<string>(Commands.Keys);
            names.Sort(System.StringComparer.Ordinal);
            foreach (string name in names)
            {
                CommandSpec spec = Commands[name];
                var builder = new System.Text.StringBuilder();
                builder.Append("[DSH Bridge] ").Append(spec.Name).Append(" - ").Append(spec.Summary);
                if (spec.Params != null && spec.Params.Length > 0)
                {
                    builder.Append(" | params: ").Append(string.Join(", ", spec.Params));
                }
                if (!string.IsNullOrEmpty(spec.Example)) builder.Append(" | example: ").Append(spec.Example);
                UnityEngine.Debug.Log(builder.ToString());
            }
        }
    }
}
