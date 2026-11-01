using System.IO;
using UnityEditor;
using UnityEngine;

namespace Dsh.UnityBridge
{
    /// <summary>Small inspector for the loopback bridge: confirms the port and lets you restart it.</summary>
    internal sealed class BridgeWindow : EditorWindow
    {
        Vector2 _scroll;
        string _probe;

        [MenuItem("Tools/DSH/Unity Bridge", false, 1200)]
        static void Open()
        {
            BridgeWindow window = GetWindow<BridgeWindow>(false, "DSH Bridge", true);
            window.minSize = new Vector2(420f, 260f);
            window.Show();
        }

        void OnEnable()
        {
            EditorApplication.update += Repaint;
        }

        void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Bridge", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Row("State", BridgeServer.IsRunning ? "listening" : "stopped");
                Row("Endpoint", BridgeServer.IsRunning ? "127.0.0.1:" + BridgeServer.Port : "-");
                Row("Started", string.IsNullOrEmpty(BridgeServer.StartedAt) ? "-" : BridgeServer.StartedAt);
                Row("Requests", BridgeServer.Requests.ToString());
                Row("Last command", string.IsNullOrEmpty(BridgeServer.LastCommand) ? "-" : BridgeServer.LastCommand);
                Row("Bridge version", BridgeDiscovery.BridgeVersion + " (protocol " + BridgeDiscovery.ProtocolVersion + ")");
                Row("Unity version", Application.unityVersion);
                Row("Project", BridgeDiscovery.ProjectRoot);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Discovery", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Row("File", BridgeDiscovery.PublishPath);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reveal folder"))
                    {
                        string dir = Path.GetDirectoryName(BridgeDiscovery.InstancesDir);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) EditorUtility.RevealInFinder(dir);
                    }
                    if (GUILayout.Button("Copy port") && BridgeServer.IsRunning)
                    {
                        GUIUtility.systemCopyBuffer = BridgeServer.Port.ToString();
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Commands", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Row("Registered", BridgeCommands.Commands.Count.ToString());
                if (GUILayout.Button("Print to console")) BridgeCommands.LogAll();
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Restart bridge"))
                {
                    BridgeServer.Shutdown();
                    BridgeServer.Start();
                    _probe = "restarted at " + System.DateTime.Now.ToString("HH:mm:ss");
                }
                if (GUILayout.Button("Self test"))
                {
                    _probe = RunSelfTest();
                }
            }

            if (!string.IsNullOrEmpty(_probe))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_probe, MessageType.None);
            }

            EditorGUILayout.EndScrollView();
        }

        static string RunSelfTest()
        {
            try
            {
                object status = BridgeCommands.Invoke("status", Args.Empty);
                return "status ok: " + Json.Serialize(status).Length.ToString() + " bytes of JSON";
            }
            catch (System.Exception e)
            {
                return "status failed: " + e.Message;
            }
        }

        static void Row(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(110f));
                EditorGUILayout.SelectableLabel(value, EditorStyles.label, GUILayout.Height(18f));
            }
        }
    }
}
