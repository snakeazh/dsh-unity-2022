using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Dsh.UnityBridge
{
    /// <summary>Rolling in-memory capture of Unity console output, both threads.</summary>
    internal static class BridgeLogs
    {
        const int Capacity = 2000;

        sealed class Entry
        {
            public long Index;
            public string Level;
            public string Message;
            public string StackTrace;
            public string Time;
            public int Frame;
        }

        static readonly List<Entry> _entries = new List<Entry>(Capacity);
        static readonly object _gate = new object();
        static long _nextIndex = 1;
        static bool _installed;
        static long _dropped;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                var entry = new Entry();
                entry.Level = LevelOf(type);
                entry.Message = condition ?? string.Empty;
                entry.StackTrace = stackTrace;
                entry.Time = DateTime.Now.ToString("HH:mm:ss.fff");
                entry.Frame = Time.frameCount;

                lock (_gate)
                {
                    entry.Index = _nextIndex++;
                    _entries.Add(entry);
                    if (_entries.Count > Capacity)
                    {
                        int overflow = _entries.Count - Capacity;
                        _entries.RemoveRange(0, overflow);
                        _dropped += overflow;
                    }
                }
            }
            catch (Exception) { }
        }

        static string LevelOf(LogType type)
        {
            switch (type)
            {
                case LogType.Warning: return "warning";
                case LogType.Error: return "error";
                case LogType.Assert: return "assert";
                case LogType.Exception: return "exception";
                default: return "log";
            }
        }

        public static long NextIndex
        {
            get { lock (_gate) return _nextIndex; }
        }

        public static long Dropped
        {
            get { lock (_gate) return _dropped; }
        }

        public static List<object> Query(long sinceIndex, string level, int limit, bool includeStackTrace)
        {
            if (limit <= 0) limit = 200;
            var result = new List<object>();
            lock (_gate)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    if (sinceIndex > 0 && e.Index < sinceIndex) continue;
                    if (!Matches(e.Level, level)) continue;
                    var item = new Dictionary<string, object>(StringComparer.Ordinal);
                    item["index"] = e.Index;
                    item["level"] = e.Level;
                    item["message"] = e.Message;
                    item["time"] = e.Time;
                    item["frame"] = e.Frame;
                    if (includeStackTrace && !string.IsNullOrEmpty(e.StackTrace)) item["stackTrace"] = e.StackTrace;
                    result.Add(item);
                    if (result.Count >= limit) break;
                }
            }
            return result;
        }

        static bool Matches(string entryLevel, string filter)
        {
            if (string.IsNullOrEmpty(filter) || filter == "all") return true;
            if (filter == "error") return entryLevel == "error" || entryLevel == "exception" || entryLevel == "assert";
            if (filter == "warning") return entryLevel == "warning" || entryLevel == "error" || entryLevel == "exception" || entryLevel == "assert";
            return entryLevel == filter;
        }

        public static int Clear()
        {
            lock (_gate)
            {
                int count = _entries.Count;
                _entries.Clear();
                _dropped = 0;
                return count;
            }
        }
    }
}
