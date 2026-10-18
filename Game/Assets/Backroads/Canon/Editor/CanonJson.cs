using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Backroads.Canon.Editor
{
    /// <summary>
    /// Minimal JSON reader for the canon files: objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double (parsed with the round-trip parser, so JSON.stringify output comes back as the exact double),
    /// plus string, bool and null. Own copy so this editor assembly does not depend on Backroads.Build.Editor (which
    /// calls CanonImporter.EnsureImported before every build).
    /// </summary>
    public static class CanonJson
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.Ws();
            var v = p.Value();
            p.Ws();
            if (p.Pos != text.Length) throw p.Error("trailing characters");
            return v;
        }

        public static Dictionary<string, object> Obj(object o, string what = "value") =>
            o as Dictionary<string, object> ?? throw new FormatException($"{what}: expected a JSON object");

        public static List<object> Arr(object o, string what = "value") =>
            o as List<object> ?? throw new FormatException($"{what}: expected a JSON array");

        public static object Get(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) ? v : null;
        public static bool Has(Dictionary<string, object> o, string key) => o != null && o.ContainsKey(key) && o[key] != null;

        public static double Num(object o, double fallback = double.NaN) => o is double d ? d : o is bool b ? (b ? 1 : 0) : fallback;
        public static double Num(Dictionary<string, object> o, string key, double fallback = double.NaN) => Num(Get(o, key), fallback);
        public static float F(Dictionary<string, object> o, string key, float fallback = float.NaN) => (float)Num(Get(o, key), fallback);
        public static int I(Dictionary<string, object> o, string key, int fallback = -1) => o != null && Get(o, key) is double d ? (int)d : fallback;
        public static string Str(Dictionary<string, object> o, string key) => Get(o, key) as string;
        public static bool Bool(Dictionary<string, object> o, string key) => Get(o, key) is bool b && b;

        public static double[] Doubles(object o)
        {
            if (!(o is List<object> l)) return Array.Empty<double>();
            var r = new double[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Num(l[i]);
            return r;
        }

        public static float[] Floats(object o)
        {
            if (!(o is List<object> l)) return Array.Empty<float>();
            var r = new float[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = (float)Num(l[i]);
            return r;
        }

        public static int[] Ints(object o)
        {
            if (!(o is List<object> l)) return Array.Empty<int>();
            var r = new int[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = (int)Num(l[i], -1);
            return r;
        }

        public static string[] Strings(object o)
        {
            if (!(o is List<object> l)) return Array.Empty<string>();
            var r = new string[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = l[i] as string;
            return r;
        }

        sealed class Parser
        {
            readonly string _s;
            public int Pos;
            readonly StringBuilder _sb = new StringBuilder();

            public Parser(string s) { _s = s; }

            public FormatException Error(string what) => new FormatException($"JSON {what} at {Pos}");

            public void Ws()
            {
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (c == ' ' || c == '\n' || c == '\r' || c == '\t') Pos++;
                    else break;
                }
            }

            public object Value()
            {
                if (Pos >= _s.Length) throw Error("unexpected end");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return String();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return Number();
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw Error("expected " + word);
                Pos += word.Length;
            }

            Dictionary<string, object> Object()
            {
                var d = new Dictionary<string, object>();
                Pos++;
                Ws();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    Ws();
                    if (_s[Pos] != '"') throw Error("expected a key");
                    var k = String();
                    Ws();
                    if (_s[Pos] != ':') throw Error("expected ':'");
                    Pos++;
                    Ws();
                    d[k] = Value();
                    Ws();
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw Error("expected ',' or '}'");
                }
            }

            List<object> Array()
            {
                var l = new List<object>();
                Pos++;
                Ws();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value());
                    Ws();
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return l; }
                    throw Error("expected ',' or ']'");
                }
            }

            string String()
            {
                Pos++;
                _sb.Clear();
                while (true)
                {
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return _sb.ToString();
                    if (c != '\\') { _sb.Append(c); continue; }
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': _sb.Append('"'); break;
                        case '\\': _sb.Append('\\'); break;
                        case '/': _sb.Append('/'); break;
                        case 'b': _sb.Append('\b'); break;
                        case 'f': _sb.Append('\f'); break;
                        case 'n': _sb.Append('\n'); break;
                        case 'r': _sb.Append('\r'); break;
                        case 't': _sb.Append('\t'); break;
                        case 'u':
                            _sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw Error("bad escape");
                    }
                }
            }

            double Number()
            {
                int start = Pos;
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') Pos++;
                    else break;
                }
                if (start == Pos) throw Error("unexpected character '" + _s[Pos] + "'");
                return double.Parse(_s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
