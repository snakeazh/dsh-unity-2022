using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Dsh.UnityBridge
{
    /// <summary>
    /// Publishes one small JSON file per running editor under
    /// %LOCALAPPDATA%/DSHUnityBridge/instances so the host side can discover live
    /// editors (including the new port after every domain reload) without a fixed port.
    /// </summary>
    internal static class BridgeDiscovery
    {
        internal const string BridgeVersion = "0.1.0";
        internal const string ProtocolVersion = "1";

        public static string ProjectRoot
        {
            get
            {
                string dataPath = Application.dataPath;
                return Path.GetFullPath(Path.Combine(dataPath, ".."));
            }
        }

        public static string RootDir
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(local, "DSHUnityBridge");
            }
        }

        public static string InstancesDir
        {
            get { return Path.Combine(RootDir, "instances"); }
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { full = path; }
            full = full.Replace('\\', '/');
            while (full.Length > 1 && full.EndsWith("/", StringComparison.Ordinal))
                full = full.Substring(0, full.Length - 1);
            return full.ToLowerInvariant();
        }

        public static string KeyFor(string projectPath)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(projectPath)));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string FileFor(string projectPath)
        {
            return Path.Combine(InstancesDir, KeyFor(projectPath) + ".json");
        }

        public static string PublishPath
        {
            get { return FileFor(ProjectRoot); }
        }

        public static void Publish(int port)
        {
            Directory.CreateDirectory(InstancesDir);

            var payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["bridgeVersion"] = BridgeVersion;
            payload["protocol"] = ProtocolVersion;
            payload["projectPath"] = ProjectRoot;
            payload["projectName"] = new DirectoryInfo(ProjectRoot).Name;
            payload["unityVersion"] = Application.unityVersion;
            payload["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            payload["host"] = "127.0.0.1";
            payload["port"] = port;
            payload["publishedAt"] = DateTime.UtcNow.ToString("o");

            string target = PublishPath;
            string temp = target + ".tmp";
            File.WriteAllText(temp, Json.Serialize(payload), new UTF8Encoding(false));
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }

        public static void Retract()
        {
            try
            {
                string target = PublishPath;
                if (File.Exists(target)) File.Delete(target);
            }
            catch (Exception) { }
        }

        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch (Exception) { return false; }
        }
    }
}
