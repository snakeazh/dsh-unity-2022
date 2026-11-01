using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dsh.UnityBridge
{
    /// <summary>Error surfaced to the client as a structured failure instead of a stack trace.</summary>
    internal sealed class BridgeException : Exception
    {
        public string Code { get; private set; }

        public BridgeException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>Typed, forgiving accessors over the raw "params" JSON object.</summary>
    internal sealed class Args
    {
        readonly Dictionary<string, object> _values;

        public Args(Dictionary<string, object> values)
        {
            _values = values ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public static readonly Args Empty = new Args(null);

        public bool Has(string key)
        {
            object value;
            return _values.TryGetValue(key, out value) && value != null;
        }

        public object Raw(string key)
        {
            object value;
            return _values.TryGetValue(key, out value) ? value : null;
        }

        public IEnumerable<string> Keys
        {
            get { return _values.Keys; }
        }

        public string Str(string key, string fallback = null)
        {
            object value = Raw(key);
            if (value == null) return fallback;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public string RequireStr(string key)
        {
            string value = Str(key, null);
            if (string.IsNullOrEmpty(value))
                throw new BridgeException("missing_param", "Missing required parameter '" + key + "'.");
            return value;
        }

        public int Int(string key, int fallback)
        {
            object value = Raw(key);
            if (value == null) return fallback;
            if (value is long) return checked((int)(long)value);
            if (value is double) return (int)Math.Round((double)value);
            int parsed;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }

        public int RequireInt(string key)
        {
            if (!Has(key)) throw new BridgeException("missing_param", "Missing required parameter '" + key + "'.");
            return Int(key, 0);
        }

        public float Float(string key, float fallback)
        {
            object value = Raw(key);
            if (value == null) return fallback;
            if (value is double) return (float)(double)value;
            if (value is long) return (float)(long)value;
            float parsed;
            if (float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }

        public bool Bool(string key, bool fallback)
        {
            object value = Raw(key);
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) return fallback;
            bool parsed;
            return bool.TryParse(text, out parsed) ? parsed : fallback;
        }

        public List<object> Arr(string key)
        {
            return Raw(key) as List<object>;
        }

        public Dictionary<string, object> Dict(string key)
        {
            return Raw(key) as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>Accepts {x,y,z} objects, [x,y,z] arrays, or a comma separated string.</summary>
        public UnityEngine.Vector3? Vec3(string key)
        {
            object value = Raw(key);
            if (value == null) return null;

            var dict = value as Dictionary<string, object>;
            if (dict != null)
                return new UnityEngine.Vector3(Num(dict, "x"), Num(dict, "y"), Num(dict, "z"));

            var list = value as List<object>;
            if (list != null)
            {
                if (list.Count == 0) return null;
                return new UnityEngine.Vector3(NumAt(list, 0), NumAt(list, 1), NumAt(list, 2));
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(text))
            {
                string[] parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    return new UnityEngine.Vector3(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
            }
            return null;
        }

        public UnityEngine.Vector2? Vec2(string key)
        {
            UnityEngine.Vector3? v = Vec3(key);
            if (!v.HasValue) return null;
            return new UnityEngine.Vector2(v.Value.x, v.Value.y);
        }

        public UnityEngine.Quaternion? Quat(string key)
        {
            object value = Raw(key);
            if (value == null) return null;
            var dict = value as Dictionary<string, object>;
            if (dict != null && dict.ContainsKey("w"))
                return new UnityEngine.Quaternion(Num(dict, "x"), Num(dict, "y"), Num(dict, "z"), Num(dict, "w"));
            // Euler angles.
            UnityEngine.Vector3? euler = Vec3(key);
            if (!euler.HasValue) return null;
            return UnityEngine.Quaternion.Euler(euler.Value);
        }

        static float Num(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict.TryGetValue(key, out value) ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : 0f;
        }

        static float NumAt(List<object> list, int index)
        {
            return index < list.Count ? Convert.ToSingle(list[index], CultureInfo.InvariantCulture) : 0f;
        }

        static float ParseFloat(string text)
        {
            float parsed;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0f;
        }

        public static double AsDouble(object value)
        {
            if (value == null) return 0d;
            if (value is double) return (double)value;
            if (value is long) return (long)value;
            if (value is bool) return ((bool)value) ? 1d : 0d;
            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0d;
        }
    }
}
