using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ModKit.Core
{
    /// <summary>
    /// Minimal JSON (RFC 8259) reader/writer. Objects become
    /// Dictionary&lt;string,object&gt;, arrays List&lt;object&gt;, numbers double,
    /// plus string/bool/null. Kept dependency free on purpose: the toolkit has to
    /// stay a plain .NET 4.0 BCL build with no NuGet.
    /// </summary>
    public static class Json
    {
        // ---------------------------------------------------------------- parse

        public static object Parse(string text)
        {
            if (text == null) return null;
            var reader = new Reader(text);
            reader.SkipWhitespace();
            object value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw reader.Error("unexpected trailing data");
            return value;
        }

        public static bool TryParse(string text, out object value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        public static string GetString(Dictionary<string, object> obj, string key, string fallback)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return fallback;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long GetLong(Dictionary<string, object> obj, string key, long fallback)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (long)(double)v;
            if (v is long) return (long)v;
            long parsed;
            if (long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed)) return parsed;
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            object v;
            if (obj == null || !obj.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s)
            {
                _s = s;
            }

            public bool AtEnd { get { return _i >= _s.Length; } }

            public FormatException Error(string message)
            {
                return new FormatException("JSON: " + message + " at offset " + _i);
            }

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            public object ReadValue()
            {
                SkipWhitespace();
                if (AtEnd) throw Error("unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't':
                        Expect("true");
                        return true;
                    case 'f':
                        Expect("false");
                        return false;
                    case 'n':
                        Expect("null");
                        return null;
                    default:
                        return ReadNumber();
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Error("expected property name");
                    string key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':') throw Error("expected ':'");
                    _i++;
                    result[key] = ReadValue();
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (c == '}')
                    {
                        _i++;
                        return result;
                    }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                _i++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    result.Add(ReadValue());
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (c == ']')
                    {
                        _i++;
                        return result;
                    }
                    throw Error("expected ',' or ']'");
                }
            }

            private char Peek()
            {
                if (AtEnd) throw Error("unexpected end of input");
                return _s[_i];
            }

            private void Expect(string literal)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                    throw Error("expected '" + literal + "'");
                _i += literal.Length;
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                _i++; // opening quote
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (AtEnd) throw Error("unterminated escape");
                    char e = _s[_i++];
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
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("truncated \\u escape");
                            sb.Append((char)ushort.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error("unknown escape '\\" + e + "'");
                    }
                }
            }

            private object ReadNumber()
            {
                int start = _i;
                if (Peek() == '-') _i++;
                while (!AtEnd && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' ||
                                  _s[_i] == 'E' || _s[_i] == '+' || _s[_i] == '-')) _i++;
                if (_i == start) throw Error("unexpected character '" + _s[start] + "'");
                string text = _s.Substring(start, _i - start);
                double value;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw Error("bad number '" + text + "'");
                if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0 &&
                    value >= long.MinValue && value <= long.MaxValue)
                    return (long)value;
                return value;
            }
        }

        // ---------------------------------------------------------------- write

        public static string Stringify(object value, bool pretty)
        {
            var sb = new StringBuilder();
            Write(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, bool pretty, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            var s = value as string;
            if (s != null)
            {
                WriteString(sb, s);
                return;
            }
            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is int || value is long || value is short || value is byte)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is double || value is float || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                bool first = true;
                foreach (var pair in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, pretty, depth + 1);
                    WriteString(sb, pair.Key);
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, pair.Value, pretty, depth + 1);
                }
                NewLine(sb, pretty, depth);
                sb.Append('}');
                return;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                bool first = true;
                sb.Append('[');
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, pretty, depth + 1);
                    Write(sb, item, pretty, depth + 1);
                }
                if (!first) NewLine(sb, pretty, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void NewLine(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u007f')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------- helpers

        /// <summary>Builds an object literal quickly: O("a", 1, "b", "x").</summary>
        public static Dictionary<string, object> O(params object[] keysAndValues)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < keysAndValues.Length; i += 2)
                dict[Convert.ToString(keysAndValues[i], CultureInfo.InvariantCulture)] = keysAndValues[i + 1];
            return dict;
        }

        public static List<object> A(params object[] items)
        {
            return new List<object>(items);
        }
    }
}
