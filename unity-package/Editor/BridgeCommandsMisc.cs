using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dsh.UnityBridge
{
    /// <summary>Commands around files, play mode, capture, logs, eval and builds.</summary>
    internal static partial class BridgeCommands
    {
        static string ResolveProjectPath(string path)
        {
            string root = Path.GetFullPath(BridgeDiscovery.ProjectRoot);
            string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeException("outside_project",
                    "'" + full + "' is outside the Unity project at '" + root + "'. Pass a path inside the project.");
            }
            return full;
        }

        static Dictionary<string, object> PlayState()
        {
            var d = D();
            d["isPlaying"] = EditorApplication.isPlaying;
            d["isPaused"] = EditorApplication.isPaused;
            d["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode;
            d["isCompiling"] = EditorApplication.isCompiling;
            d["frameCount"] = Time.frameCount;
            d["timeSinceStartup"] = (double)Time.realtimeSinceStartup;
            Scene scene = SceneManager.GetActiveScene();
            d["activeScene"] = scene.IsValid() ? scene.name : null;
            return d;
        }

        // --------------------------------------------------------------------- files

        static object CmdWriteFile(Args a)
        {
            string requested = a.RequireStr("path");
            string full = ResolveProjectPath(requested);
            string content = a.Str("content", string.Empty);
            bool append = a.Bool("append", false);
            bool refresh = a.Bool("refresh", true);

            string directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var utf8 = new UTF8Encoding(false);
            if (append) File.AppendAllText(full, content, utf8);
            else File.WriteAllText(full, content, utf8);

            if (refresh) AssetDatabase.Refresh();

            var result = D();
            result["path"] = full.Substring(BridgeDiscovery.ProjectRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            result["fullPath"] = full;
            result["bytes"] = new FileInfo(full).Length;
            result["appended"] = append;
            result["refreshed"] = refresh;
            if (refresh) result["isCompiling"] = EditorApplication.isCompiling;
            return result;
        }

        static object CmdImportPackage(Args a)
        {
            string requested = a.RequireStr("path");
            string full = ResolveProjectPath(requested);
            if (!File.Exists(full)) throw new BridgeException("not_found", "No .unitypackage at '" + full + "'.");

            AssetDatabase.ImportPackage(full, false);

            var result = D();
            result["package"] = full;
            result["importing"] = true;
            result["hint"] = "Import runs asynchronously; poll status and wait until isCompiling/isUpdating are false.";
            return result;
        }

        // ---------------------------------------------------------------- play mode

        static object CmdPlay(Args a)
        {
            string action = a.Str("action", "play").Trim().ToLowerInvariant();
            switch (action)
            {
                case "play":
                case "start":
                    if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
                    break;
                case "stop":
                case "exit":
                    if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
                    break;
                case "pause":
                    EditorApplication.isPaused = true;
                    break;
                case "resume":
                case "unpause":
                    EditorApplication.isPaused = false;
                    break;
                case "step":
                    if (!EditorApplication.isPlaying)
                    {
                        throw new BridgeException("not_playing", "Cannot step while the editor is not in play mode. Send action 'play' first.");
                    }
                    EditorApplication.isPaused = true;
                    EditorApplication.Step();
                    break;
                default:
                    throw new BridgeException("invalid_param",
                        "Unknown play action '" + action + "'. Use play, stop, pause, resume or step.");
            }

            Dictionary<string, object> state = PlayState();
            state["action"] = action;
            if (action == "play" || action == "stop")
                state["hint"] = "Entering or leaving play mode triggers a script reload and restarts the bridge; retry status if this call times out.";
            return state;
        }

        // ------------------------------------------------------------------ capture

        static object CmdScreenshot(Args a)
        {
            string view = a.Str("view", "scene").Trim().ToLowerInvariant();
            int width = Math.Max(16, a.Int("width", 1280));
            int height = Math.Max(16, a.Int("height", 720));

            // Optional framing target: the Scene view is pointed at it, and the game view falls
            // back to a temporary camera when the scene has no enabled camera of its own.
            string targetPath = a.Str("target", null);
            GameObject framed = string.IsNullOrEmpty(targetPath) ? null : SceneSearch.Resolve(a, "target");
            Bounds bounds = framed == null ? default(Bounds) : BoundsOf(framed);

            Camera camera;
            string source;
            bool temporary = false;
            if (view == "scene" || view == "sceneview" || view == "editor")
            {
                // Rendering the Editor's own Scene view camera is unreliable: its transform only
                // follows the pivot/size on the next repaint, so capturing in the same tick renders
                // the previous framing. A throwaway camera over the same contents is deterministic
                // and also works in `-nographics -batchmode` runs.
                Bounds framedBounds = framed != null ? bounds : SceneBounds();
                camera = CreateFramingCamera(framedBounds);
                temporary = true;
                source = "framing-camera";
            }
            else if (view == "game" || view == "camera")
            {
                camera = Camera.main;
                if (camera == null)
                {
                    Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                    for (int i = 0; i < cameras.Length; i++)
                    {
                        if (cameras[i] != null && cameras[i].isActiveAndEnabled) { camera = cameras[i]; break; }
                    }
                }
                if (camera == null && framed != null)
                {
                    camera = CreateFramingCamera(bounds);
                    temporary = true;
                }
                source = "game-camera";
            }
            else
            {
                throw new BridgeException("invalid_param", "view must be 'scene' or 'game', got '" + view + "'.");
            }

            if (camera == null)
                throw new BridgeException("no_camera",
                    "No camera is available for view '" + view + "'. Add a Camera to the scene, or pass a target so a temporary camera can frame it.");

            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            byte[] png;
            string cameraName = camera.name;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;

                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                    texture.Apply(false);
                    png = texture.EncodeToPNG();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                if (temporary) UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }

            if (png == null) throw new BridgeException("capture_failed", "The camera produced no pixels.");

            string requested = a.Str("path", null);
            string full = string.IsNullOrEmpty(requested)
                ? Path.Combine(BridgeDiscovery.ProjectRoot, "Temp", "DSHUnityBridge", "screenshots",
                    "screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png")
                : ResolveProjectPath(requested);

            string directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(full, png);

            var result = D();
            result["path"] = full;
            result["relativePath"] = full.Substring(BridgeDiscovery.ProjectRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            result["bytes"] = png.Length;
            result["width"] = width;
            result["height"] = height;
            result["view"] = view;
            result["source"] = source;
            result["camera"] = cameraName;
            result["temporaryCamera"] = temporary;
            if (framed != null)
            {
                result["target"] = SceneSearch.PathOf(framed);
                result["framedBounds"] = new Dictionary<string, object>
                {
                    { "center", Vec(bounds.center) },
                    { "size", Vec(bounds.size) }
                };
            }
            return result;
        }

        /// <summary>World bounds of a GameObject: its renderers, else a 1m box at its origin.</summary>
        static Bounds BoundsOf(GameObject go)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            bool first = true;
            Bounds bounds = new Bounds(go.transform.position, Vector3.one);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (first) { bounds = renderer.bounds; first = false; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        /// <summary>World bounds of everything visible in the loaded scenes, else a 10m box.</summary>
        static Bounds SceneBounds()
        {
            bool first = true;
            Bounds bounds = new Bounds(Vector3.zero, new Vector3(10f, 10f, 10f));
            foreach (GameObject root in SceneSearch.All(true))
            {
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null) continue;
                    if (first) { bounds = renderer.bounds; first = false; }
                    else bounds.Encapsulate(renderer.bounds);
                }
            }
            return bounds;
        }

        /// <summary>A throwaway camera that frames a target from a three-quarter view.</summary>
        static Camera CreateFramingCamera(Bounds bounds)
        {
            var go = new GameObject("__DshScreenshotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.11f, 0.12f, 0.15f, 1f);
            camera.fieldOfView = 45f;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.25f);
            float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.35f;
            Vector3 offset = new Vector3(0.85f, 0.65f, -0.95f).normalized * distance;
            go.transform.position = bounds.center + offset;
            go.transform.LookAt(bounds.center);
            camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 3f);
            camera.farClipPlane = distance + radius * 6f;
            return camera;
        }

        // --------------------------------------------------------------------- logs

        static object CmdGetLogs(Args a)
        {
            long since = 0;
            object rawSince = a.Raw("since");
            if (rawSince != null) since = (long)Args.AsDouble(rawSince);
            string level = a.Str("level", "all");
            int limit = Math.Max(1, a.Int("limit", 200));
            bool stackTrace = a.Bool("stackTrace", false);

            List<object> entries = BridgeLogs.Query(since, level, limit, stackTrace);

            var result = D();
            result["entries"] = entries;
            result["count"] = entries.Count;
            result["since"] = since;
            result["next"] = BridgeLogs.NextIndex;
            result["dropped"] = BridgeLogs.Dropped;
            result["level"] = level;
            return result;
        }

        static object CmdClearLogs(Args a)
        {
            int cleared = BridgeLogs.Clear();
            var result = D();
            result["cleared"] = cleared;
            result["next"] = BridgeLogs.NextIndex;
            return result;
        }

        // --------------------------------------------------------------------- eval

        static object CmdEval(Args a)
        {
            string code = a.RequireStr("code");
            string mode = a.Str("mode", "auto");

            return Task.Run<object>(delegate
            {
                string diagnostics;
                string assemblyPath = EvalCompiler.Compile(code, mode, out diagnostics);

                object value = BridgeServer.OnMainThread(delegate
                {
                    object raw = EvalRunner.Run(assemblyPath);
                    return Json.ToPlain(raw);
                }).GetAwaiter().GetResult();

                var result = D();
                result["result"] = value;
                result["assembly"] = assemblyPath;
                if (!string.IsNullOrEmpty(diagnostics)) result["diagnostics"] = diagnostics;
                return result;
            });
        }

        // -------------------------------------------------------------------- build

        static bool TryParseBuildTarget(string name, out BuildTarget target)
        {
            target = EditorUserBuildSettings.activeBuildTarget;
            if (string.IsNullOrEmpty(name)) return true;
            switch (name.Trim().ToLowerInvariant())
            {
                case "windows":
                case "win":
                case "win64":
                case "windows64":
                case "standalonewindows64":
                    target = BuildTarget.StandaloneWindows64;
                    return true;
                case "linux":
                case "standalonelinux64":
                    target = BuildTarget.StandaloneLinux64;
                    return true;
                case "mac":
                case "osx":
                case "macos":
                case "standaloneosx":
                    target = BuildTarget.StandaloneOSX;
                    return true;
                case "android":
                    target = BuildTarget.Android;
                    return true;
                case "ios":
                    target = BuildTarget.iOS;
                    return true;
                case "webgl":
                case "web":
                    target = BuildTarget.WebGL;
                    return true;
            }
            return Enum.TryParse(name.Trim(), true, out target);
        }

        static object CmdBuild(Args a)
        {
            BuildTarget target;
            if (!TryParseBuildTarget(a.Str("target", null), out target))
                throw new BridgeException("invalid_param", "Unknown build target '" + a.Str("target", null) + "'.");

            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (group == BuildTargetGroup.Unknown)
                throw new BridgeException("invalid_param", "Build target '" + target + "' has no build target group.");

            List<string> scenes = new List<string>();
            List<object> requested = a.Arr("scenes");
            if (requested != null)
            {
                foreach (object item in requested)
                {
                    string path = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(path)) scenes.Add(path);
                }
            }
            if (scenes.Count == 0)
            {
                EditorBuildSettingsScene[] configured = EditorBuildSettings.scenes;
                for (int i = 0; i < configured.Length; i++)
                    if (configured[i] != null && configured[i].enabled) scenes.Add(configured[i].path);
            }
            if (scenes.Count == 0)
            {
                // A freshly created project has no Build Settings entries: fall back to the scene
                // that is open, so "build this project" works without the caller naming a scene.
                Scene active = SceneManager.GetActiveScene();
                if (active.IsValid() && !string.IsNullOrEmpty(active.path)) scenes.Add(active.path);
            }
            if (scenes.Count == 0)
                throw new BridgeException("no_scenes", "No enabled scenes in Build Settings and the open scene is unsaved; pass 'scenes' explicitly.");

            string output = a.Str("output", null);
            if (string.IsNullOrEmpty(output))
            {
                string extension = target == BuildTarget.StandaloneWindows64 ? ".exe" : string.Empty;
                output = Path.Combine("Builds", target.ToString(), "game" + extension);
            }
            string fullOutput = ResolveProjectPath(output);
            string directory = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var options = new BuildPlayerOptions();
            options.scenes = scenes.ToArray();
            options.locationPathName = fullOutput;
            options.target = target;
            options.targetGroup = group;
            options.options = a.Bool("development", false) ? BuildOptions.Development : BuildOptions.None;

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            var result = D();
            result["result"] = summary.result.ToString();
            result["succeeded"] = summary.result == BuildResult.Succeeded;
            result["output"] = fullOutput;
            result["target"] = target.ToString();
            result["scenes"] = scenes.Count;
            result["totalBytes"] = (long)summary.totalSize;
            result["durationSeconds"] = summary.totalTime.TotalSeconds;
            result["errors"] = summary.totalErrors;
            result["warnings"] = summary.totalWarnings;
            return result;
        }
    }
}
