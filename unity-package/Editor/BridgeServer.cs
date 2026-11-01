using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Dsh.UnityBridge
{
    /// <summary>
    /// Loopback NDJSON server: one request per TCP connection, one JSON line per response.
    /// Requests are marshalled onto the Unity main thread; slow work returns a Task that the
    /// connection thread awaits so the editor keeps rendering.
    /// </summary>
    [InitializeOnLoad]
    internal static class BridgeServer
    {
        internal const int DefaultRequestTimeoutMs = 120000;
        const int MaxRequestBytes = 16 * 1024 * 1024;

        /// <summary>How long the main thread may go without ticking before requests fail fast.</summary>
        const int StalledMainThreadMs = 15000;
        static long _lastTick = Environment.TickCount;

        static TcpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;
        static int _port;
        static long _requests;
        static string _lastCommand = string.Empty;
        static string _startedAt;

        static readonly ConcurrentQueue<PendingCall> _calls = new ConcurrentQueue<PendingCall>();
        static readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        static BridgeServer()
        {
            EditorApplication.delayCall += Boot;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        /// <summary>
        /// Fires on every domain load, including the very first one after the package is imported.
        /// The explicit log line makes it obvious in Editor.log whether the bridge assembly is
        /// loaded at all, which separates "package not compiled" from "listener failed to start".
        /// </summary>
        [InitializeOnLoadMethod]
        static void Announce()
        {
            Debug.Log("[DSH Bridge] assembly loaded for " + BridgeDiscovery.ProjectRoot + " (unity " + Application.unityVersion + ")");
            Boot();
        }

        internal static bool IsRunning { get { return _running; } }
        internal static int Port { get { return _port; } }
        internal static long Requests { get { return Interlocked.Read(ref _requests); } }
        internal static string LastCommand { get { return _lastCommand; } }
        internal static string StartedAt { get { return _startedAt; } }

        static void Boot()
        {
            BridgeLogs.Install();
            Start();
        }

        internal static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch (Exception e)
            {
                Debug.LogError("[DSH Bridge] could not open a loopback port: " + e.Message);
                return;
            }

            _running = true;
            _startedAt = DateTime.UtcNow.ToString("o");
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "dsh-unity-bridge-accept";
            _acceptThread.Start();

            try
            {
                BridgeDiscovery.Publish(_port);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DSH Bridge] could not publish discovery file: " + e.Message);
            }

            Debug.Log("[DSH Bridge] listening on 127.0.0.1:" + _port + " for " + BridgeDiscovery.ProjectRoot +
                      " (discovery: " + BridgeDiscovery.PublishPath + ")");
        }

        internal static void Shutdown()
        {
            _running = false;
            try { EditorApplication.update -= Pump; } catch (Exception) { }
            try { BridgeDiscovery.Retract(); } catch (Exception) { }
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            _listener = null;
        }

        /// <summary>Runs work on the Unity main thread and returns its boxed result.</summary>
        internal static Task<object> OnMainThread(Func<object> work)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadActions.Enqueue(delegate
            {
                try { tcs.TrySetResult(work()); }
                catch (Exception e) { tcs.TrySetException(e); }
            });
            return tcs.Task;
        }

        // ------------------------------------------------------------- transport

        static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (SocketException)
                {
                    if (!_running) return;
                    Thread.Sleep(20);
                    continue;
                }
                catch (Exception)
                {
                    if (!_running) return;
                    Thread.Sleep(50);
                    continue;
                }

                var worker = new Thread(delegate () { HandleClient(client); });
                worker.IsBackground = true;
                worker.Name = "dsh-unity-bridge-client";
                worker.Start();
            }
        }

        static void HandleClient(TcpClient client)
        {
            Stream stream = null;
            try
            {
                client.NoDelay = true;
                stream = client.GetStream();
                string line = ReadLine(stream);
                if (string.IsNullOrEmpty(line))
                {
                    return;
                }

                Dictionary<string, object> request;
                try
                {
                    request = Json.Parse(line) as Dictionary<string, object>;
                }
                catch (Exception e)
                {
                    WriteLine(stream, Failure(null, "bad_request", "Request is not valid JSON: " + e.Message, null, 0));
                    return;
                }
                if (request == null)
                {
                    WriteLine(stream, Failure(null, "bad_request", "Request must be a JSON object.", null, 0));
                    return;
                }

                var call = new PendingCall(request);

                // A modal dialog (save prompt, progress with "cancel", license window) stops the
                // main thread from ticking, so the request would sit in the queue until the client
                // times out. Fail fast with an actionable message instead.
                long stalled = Environment.TickCount - Interlocked.Read(ref _lastTick);
                if (stalled > StalledMainThreadMs)
                {
                    WriteLine(stream, Failure(call.Id, "editor_blocked",
                        "The editor main thread has not ticked for " + (stalled / 1000) + "s. A modal dialog or a long synchronous import is probably blocking it; the request was not executed.",
                        null, 0));
                    return;
                }

                _calls.Enqueue(call);
                if (!call.Ready.Wait(call.TimeoutMs))
                {
                    WriteLine(stream, Failure(call.Id, "timeout",
                        "The editor did not answer within " + call.TimeoutMs + " ms. It is probably compiling, importing, or building.", null, 0));
                    return;
                }

                string response = call.ResponseLine;
                if (response == null && call.Async != null)
                {
                    try
                    {
                        if (!call.Async.Wait(call.TimeoutMs))
                        {
                            WriteLine(stream, Failure(call.Id, "timeout",
                                "The command did not finish within " + call.TimeoutMs + " ms.", null, call.ElapsedMs));
                            return;
                        }
                        object result = call.Async.Result;
                        response = Success(call.Id, result, call.ElapsedMs);
                    }
                    catch (BridgeException be)
                    {
                        response = Failure(call.Id, be.Code, be.Message, null, call.ElapsedMs);
                    }
                    catch (Exception e)
                    {
                        Exception inner = Unwrap(e);
                        BridgeException bridgeError = inner as BridgeException;
                        response = bridgeError != null
                            ? Failure(call.Id, bridgeError.Code, bridgeError.Message, null, call.ElapsedMs)
                            : Failure(call.Id, "command_failed", inner.Message, inner.ToString(), call.ElapsedMs);
                    }
                }

                if (response == null)
                {
                    response = Failure(call.Id, "internal_error", "The bridge produced no response.", null, call.ElapsedMs);
                }

                // Publish "when busy, retry" hints for the client.
                WriteLine(stream, response);
            }
            catch (Exception e)
            {
                try
                {
                    if (stream != null)
                        WriteLine(stream, Failure(null, "transport_error", e.Message, e.ToString(), 0));
                }
                catch (Exception) { }
            }
            finally
            {
                try { if (stream != null) stream.Dispose(); } catch (Exception) { }
                try { client.Close(); } catch (Exception) { }
            }
        }

        static Exception Unwrap(Exception e)
        {
            var aggregate = e as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count == 1) return Unwrap(aggregate.InnerExceptions[0]);
            var invocation = e as System.Reflection.TargetInvocationException;
            if (invocation != null && invocation.InnerException != null) return Unwrap(invocation.InnerException);
            return e;
        }

        static string ReadLine(Stream stream)
        {
            var buffer = new MemoryStream(4096);
            var chunk = new byte[8192];
            while (buffer.Length < MaxRequestBytes)
            {
                int read;
                try { read = stream.Read(chunk, 0, chunk.Length); }
                catch (IOException) { break; }
                if (read <= 0) break;
                int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
                if (newline >= 0)
                {
                    buffer.Write(chunk, 0, newline);
                    break;
                }
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length == 0) return null;
            return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        }

        static void WriteLine(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        // ------------------------------------------------------------- dispatch

        static void Pump()
        {
            Interlocked.Exchange(ref _lastTick, Environment.TickCount);

            Action action;
            int guard = 0;
            while (guard++ < 8 && _mainThreadActions.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError("[DSH Bridge] main thread action failed: " + e); }
            }

            PendingCall call;
            guard = 0;
            while (guard++ < 8 && _calls.TryDequeue(out call))
                Execute(call);
        }

        static void Execute(PendingCall call)
        {
            string command = call.Command;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                object result = BridgeCommands.Invoke(command, new Args(call.Params));
                var task = result as Task<object>;
                if (task != null)
                {
                    call.Async = task;
                }
                else
                {
                    call.ResponseLine = Success(call.Id, Json.ToPlain(result), (long)watch.Elapsed.TotalMilliseconds);
                }
            }
            catch (BridgeException be)
            {
                call.ResponseLine = Failure(call.Id, be.Code, be.Message, null, (long)watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception e)
            {
                Exception inner = Unwrap(e);
                call.ResponseLine = Failure(call.Id, "command_failed", inner.Message, inner.ToString(), (long)watch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                Interlocked.Increment(ref _requests);
                _lastCommand = command;
                call.Ready.Set();
            }
        }

        static string Success(string id, object result, long elapsedMs)
        {
            var envelope = new Dictionary<string, object>(StringComparer.Ordinal);
            envelope["id"] = id;
            envelope["ok"] = true;
            envelope["ms"] = elapsedMs;
            envelope["result"] = result;
            return Json.Serialize(envelope);
        }

        static string Failure(string id, string code, string message, string details, long elapsedMs)
        {
            var error = new Dictionary<string, object>(StringComparer.Ordinal);
            error["code"] = code;
            error["message"] = message;
            if (!string.IsNullOrEmpty(details)) error["details"] = details;

            var envelope = new Dictionary<string, object>(StringComparer.Ordinal);
            envelope["id"] = id;
            envelope["ok"] = false;
            envelope["ms"] = elapsedMs;
            envelope["error"] = error;
            return Json.Serialize(envelope);
        }

        sealed class PendingCall
        {
            internal readonly Dictionary<string, object> Request;
            internal readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
            internal string ResponseLine;
            internal Task<object> Async;

            // Async commands finish after the main-thread dispatch returns, so the elapsed time
            // has to be measured from the moment the request arrived, not from the dispatch.
            readonly Stopwatch _watch = Stopwatch.StartNew();

            internal long ElapsedMs
            {
                get { return (long)_watch.Elapsed.TotalMilliseconds; }
            }

            internal PendingCall(Dictionary<string, object> request)
            {
                Request = request;
            }

            internal string Id
            {
                get
                {
                    object value;
                    return Request.TryGetValue("id", out value) && value != null ? Convert.ToString(value) : null;
                }
            }

            internal string Command
            {
                get
                {
                    object value;
                    return Request.TryGetValue("command", out value) && value != null ? Convert.ToString(value) : string.Empty;
                }
            }

            internal Dictionary<string, object> Params
            {
                get
                {
                    object value;
                    if (Request.TryGetValue("params", out value))
                    {
                        var dict = value as Dictionary<string, object>;
                        if (dict != null) return dict;
                    }
                    return new Dictionary<string, object>(StringComparer.Ordinal);
                }
            }

            internal int TimeoutMs
            {
                get
                {
                    object value;
                    if (!Request.TryGetValue("timeoutMs", out value) || value == null) return DefaultRequestTimeoutMs;
                    int parsed;
                    try { parsed = Convert.ToInt32(value); }
                    catch (Exception) { return DefaultRequestTimeoutMs; }
                    if (parsed < 1000) return 1000;
                    if (parsed > 3600000) return 3600000;
                    return parsed;
                }
            }
        }
    }
}
