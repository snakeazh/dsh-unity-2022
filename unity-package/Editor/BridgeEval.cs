using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Dsh.UnityBridge
{
    /// <summary>
    /// Compiles C# snippets against the running editor's assemblies with the editor's own Roslyn
    /// (DotNetSdkRoslyn/csc.dll) into &lt;project&gt;/Temp/DSHUnityBridge so that loading the resulting
    /// assembly never triggers an asset import or a domain reload.
    /// </summary>
    internal static class EvalCompiler
    {
        const int CompileTimeoutMs = 120000;
        static readonly TimeSpan Retention = TimeSpan.FromHours(12);

        internal static string DataPath
        {
            get { return EditorApplication.applicationContentsPath; }
        }

        public static string Compile(string code, string mode, out string diagnostics)
        {
            diagnostics = null;
            if (string.IsNullOrEmpty(code)) throw new BridgeException("missing_param", "Parameter 'code' is required.");

            string projectRoot = BridgeDiscovery.ProjectRoot;
            string root = Path.Combine(projectRoot, "Temp", "DSHUnityBridge");
            string workDir = Path.Combine(root, "eval-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(workDir);
            Cleanup(root, workDir);

            string sourcePath = Path.Combine(workDir, "Program.cs");
            string assemblyPath = Path.Combine(workDir, "eval.dll");
            File.WriteAllText(sourcePath, BuildSource(code, mode), new UTF8Encoding(false));

            string rspPath = Path.Combine(workDir, "csc.rsp");
            File.WriteAllText(rspPath, BuildArguments(projectRoot, sourcePath, assemblyPath), new UTF8Encoding(false));

            int exitCode;
            string output;
            string error;
            string compiler = ResolveCompiler(out error);
            if (compiler == null)
            {
                diagnostics = error;
                throw new BridgeException("compiler_missing", error);
            }

            RunCompiler(compiler, rspPath, out exitCode, out output, out error);

            if (exitCode != 0 || !File.Exists(assemblyPath))
            {
                diagnostics = string.IsNullOrEmpty(error) ? output : output + "\n" + error;
                if (string.IsNullOrEmpty(diagnostics)) diagnostics = "C# compilation failed with exit code " + exitCode + ".";
                throw new BridgeException("compile_error", diagnostics.Trim());
            }

            diagnostics = output.Trim();
            return assemblyPath;
        }

        static void Cleanup(string root, string current)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                DateTime cutoff = DateTime.UtcNow - Retention;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (Directory.GetCreationTimeUtc(dir) < cutoff) Directory.Delete(dir, true);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        static string ResolveCompiler(out string error)
        {
            error = null;
            string data = DataPath;
            string runtimeRoot = Path.Combine(data, "NetCoreRuntime");
            string dotnet = Path.Combine(runtimeRoot, "dotnet.exe");
            if (!File.Exists(dotnet)) dotnet = Path.Combine(runtimeRoot, "dotnet");
            string csc = Path.Combine(data, "DotNetSdkRoslyn", "csc.dll");

            if (File.Exists(dotnet) && File.Exists(csc)) return dotnet;

            error = "The editor's C# compiler is missing. Expected '" + csc + "' together with '" + dotnet + "'.";
            return null;
        }

        static void RunCompiler(string compiler, string rspPath, out int exitCode, out string output, out string error)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = compiler;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Path.GetDirectoryName(rspPath);
            psi.Arguments = "\"" + Path.Combine(DataPath, "DotNetSdkRoslyn", "csc.dll") + "\" @" + Quote(rspPath);

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                using (var stdoutDone = new System.Threading.ManualResetEventSlim(false))
                using (var stderrDone = new System.Threading.ManualResetEventSlim(false))
                {
                    process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) stdoutDone.Set();
                        else lock (stdout) stdout.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) stderrDone.Set();
                        else lock (stderr) stderr.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(CompileTimeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        output = stdout.ToString();
                        error = "C# compilation timed out after " + (CompileTimeoutMs / 1000) + "s.";
                        exitCode = -1;
                        return;
                    }

                    stdoutDone.Wait(5000);
                    stderrDone.Wait(5000);
                    exitCode = process.ExitCode;
                    output = stdout.ToString();
                    error = stderr.ToString();
                }
            }
        }

        static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        static string BuildArguments(string projectRoot, string sourcePath, string assemblyPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-nologo");
            sb.AppendLine("-target:library");
            sb.AppendLine("-langversion:9");
            sb.AppendLine("-nostdlib+");
            sb.AppendLine("-nowarn:1701,1702,0618");
            sb.AppendLine("-optimize-");
            sb.AppendLine("-debug-");
            sb.AppendLine("-define:" + string.Join(";", Defines()));
            sb.AppendLine("-out:" + Quote(assemblyPath));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string reference in References(projectRoot))
            {
                string file = Path.GetFileName(reference);
                if (!seen.Add(file)) continue;
                sb.AppendLine("-r:" + Quote(reference));
            }

            sb.AppendLine(Quote(sourcePath));
            return sb.ToString();
        }

        static IEnumerable<string> Defines()
        {
            var defines = new List<string>();
            defines.Add("UNITY_EDITOR");
            defines.Add("UNITY_2022_3_OR_NEWER");
            defines.Add("UNITY_2022_3_62");
            defines.Add("CSHARP_7_3_OR_NEWER");
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                defines.Add("UNITY_EDITOR_WIN");
                defines.Add("UNITY_STANDALONE_WIN");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                defines.Add("UNITY_EDITOR_OSX");
                defines.Add("UNITY_STANDALONE_OSX");
            }
            else
            {
                defines.Add("UNITY_EDITOR_LINUX");
                defines.Add("UNITY_STANDALONE_LINUX");
            }
            return defines;
        }

        static List<string> References(string projectRoot)
        {
            var refs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string data = DataPath;
            string managed = Path.Combine(data, "Managed");

            // Mirror what Unity itself feeds the compiler for Assembly-CSharp-Editor (see the
            // generated Library/Bee/**/Assembly-CSharp-Editor.rsp): mscorlib from the 4.8 reference
            // assemblies plus the *facade* netstandard.dll that sits next to it in Facades/, which
            // only type-forwards into mscorlib. Using NetStandard/ref/2.1.0/netstandard.dll instead
            // would give the snippet a second corlib and Roslyn reports CS0518 ("predefined type
            // 'System.Void' is not defined or imported"); leaving mscorlib out makes every call into
            // a project assembly fail with CS0012 because those are compiled against 4.8 mscorlib.
            string refAssemblies = Path.Combine(data, "UnityReferenceAssemblies", "unity-4.8-api");
            AddAll(refs, seen, refAssemblies);
            AddAll(refs, seen, Path.Combine(refAssemblies, "Facades"));

            // The engine API lives in the per-module assemblies; Managed/UnityEngine also carries
            // the small UnityEngine.dll/UnityEditor.dll facades that the modules forward from.
            // Managed/UnityEngine.dll at the top of Managed/ is the full legacy assembly: adding it
            // next to the modules makes the compiler report CS0433 ("Object is defined in
            // UnityEngine.CoreModule and UnityEngine") for every type that appears in both.
            AddAll(refs, seen, Path.Combine(managed, "UnityEngine"));
            AddAll(refs, seen, Path.Combine(managed, "UnityEditor"));

            // Project scripts and package assemblies, so snippets can touch user code.
            AddAll(refs, seen, Path.Combine(projectRoot, "Library", "ScriptAssemblies"));

            return refs;
        }

        static void AddAll(List<string> into, HashSet<string> seen, string directory, string exclude = null)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (string file in Directory.GetFiles(directory, "*.dll"))
                {
                    if (exclude != null && string.Equals(Path.GetFileName(file), exclude, StringComparison.OrdinalIgnoreCase)) continue;
                    AddIfExists(into, seen, file);
                }
            }
            catch (Exception) { }
        }

        static void AddIfExists(List<string> into, HashSet<string> seen, string file)
        {
            if (!File.Exists(file)) return;
            // Two directories can carry the same assembly name; the compiler rejects that.
            if (!seen.Add(Path.GetFileName(file))) return;
            into.Add(file);
        }

        // -------------------------------------------------------------- source

        static string BuildSource(string code, string mode)
        {
            var hoisted = new List<string>();
            string body = code;

            // Hoist leading "using X;" lines so snippets can be pasted verbatim.
            while (true)
            {
                string trimmed = body.TrimStart();
                if (!trimmed.StartsWith("using ", StringComparison.Ordinal)) break;
                int newline = trimmed.IndexOf('\n');
                string candidate = newline < 0 ? trimmed : trimmed.Substring(0, newline);
                string line = candidate.Trim();
                if (!line.EndsWith(";", StringComparison.Ordinal) || line.Contains("=")) break;
                if (line.Contains("(")) break;
                hoisted.Add(line);
                if (newline < 0) { body = string.Empty; break; }
                body = trimmed.Substring(newline + 1);
            }

            string trimmedBody = body.Trim();
            bool expression = string.Equals(mode, "expression", StringComparison.OrdinalIgnoreCase);
            if (!expression && (string.IsNullOrEmpty(mode) || string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase)))
                expression = LooksLikeExpression(trimmedBody);

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> DSH Unity Bridge eval snippet </auto-generated>");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine("using UnityEditor.SceneManagement;");
            sb.AppendLine("using UnityEngine.SceneManagement;");
            sb.AppendLine("using Object = UnityEngine.Object;");
            foreach (string directive in hoisted)
                sb.AppendLine(directive);
            sb.AppendLine();
            sb.AppendLine("namespace __DshEval");
            sb.AppendLine("{");
            sb.AppendLine("    public static class Program");
            sb.AppendLine("    {");
            sb.AppendLine("        public static object Run()");
            sb.AppendLine("        {");
            if (expression)
            {
                sb.AppendLine("            return (object)(" + trimmedBody + ");");
            }
            else
            {
                sb.AppendLine(body);
                // A snippet that returns on every path must not get an unreachable return after it.
                bool returns = System.Text.RegularExpressions.Regex.IsMatch(body, @"\breturn\b");
                if (!returns && trimmedBody.Length > 0 && !trimmedBody.EndsWith("}", StringComparison.Ordinal))
                    sb.AppendLine("            return null;");
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        static bool LooksLikeExpression(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (code.IndexOf(';') >= 0) return false;
            if (code.IndexOf('\n') >= 0) return false;
            if (code.Contains("return ")) return false;
            return true;
        }
    }

    /// <summary>Loads a compiled snippet assembly and invokes its entry point on the main thread.</summary>
    internal static class EvalRunner
    {
        public static object Run(string assemblyPath)
        {
            Assembly assembly;
            try
            {
                // Load the bytes rather than the path: every snippet compiles to the same
                // assembly identity (eval.dll), and Assembly.LoadFrom caches by identity, so
                // the second eval in one editor session would silently re-run the first one.
                assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));
            }
            catch (Exception e)
            {
                throw new BridgeException("eval_load_failed", "Could not load compiled snippet: " + e.Message);
            }

            Type type = assembly.GetType("__DshEval.Program", false);
            if (type == null) throw new BridgeException("eval_failed", "Compiled snippet has no __DshEval.Program type.");
            MethodInfo method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new BridgeException("eval_failed", "Compiled snippet has no public static object Run() method.");

            try
            {
                return method.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                if (e.InnerException != null) ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
