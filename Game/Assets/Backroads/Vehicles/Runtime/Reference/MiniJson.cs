using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Backroads.Vehicles.Reference
{
    /// <summary>
    /// A small JSON reader for the oracle's reference files (golden traces): objects become
    /// Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, plus string, bool and null.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            int i = 0;
            var v = Value(json, ref i);
            Ws(json, ref i);
            if (i != json.Length) throw new FormatException($"JSON: trailing characters at {i}");
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            return Num(s, ref i);
        }

        static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            Ws(s, ref i);
            if (s[i] == '}') { i++; return d; }
            for (;;)
            {
                Ws(s, ref i);
                string k = Str(s, ref i);
                Ws(s, ref i);
                if (s[i] != ':') throw new FormatException($"JSON: ':' expected at {i}");
                i++;
                d[k] = Value(s, ref i);
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException($"JSON: ',' or '}}' expected at {i}");
            }
        }

        static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            Ws(s, ref i);
            if (s[i] == ']') { i++; return l; }
            for (;;)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException($"JSON: ',' or ']' expected at {i}");
            }
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException($"JSON: string expected at {i}");
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: throw new FormatException($"JSON: bad escape at {i}");
                }
            }
            i++;
            return sb.ToString();
        }

        static double Num(string s, ref int i)
        {
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st) throw new FormatException($"JSON: value expected at {i}");
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ---- access helpers ----
        public static Dictionary<string, object> O(object o) => o as Dictionary<string, object> ?? throw new FormatException("JSON object expected");
        public static List<object> A(object o) => o as List<object> ?? throw new FormatException("JSON array expected");
        public static object Get(Dictionary<string, object> o, string k) => o != null && o.TryGetValue(k, out var v) ? v : null;
        public static double D(object o) => o is double d ? d : o is bool b ? (b ? 1 : 0) : double.NaN;
        public static double D(Dictionary<string, object> o, string k) => D(Get(o, k));
        public static string S(Dictionary<string, object> o, string k) => Get(o, k) as string;
    }
}
