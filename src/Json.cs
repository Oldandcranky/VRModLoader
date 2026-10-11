using System;
using System.Text;
using System.Web.Script.Serialization;

namespace VRModLoader
{
    /// <summary>Thin wrapper over the built-in JavaScriptSerializer (no external packages needed).</summary>
    internal static class Json
    {
        private static JavaScriptSerializer Make()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 64;
            return s;
        }

        public static string Serialize(object value)
        {
            return Make().Serialize(value);
        }

        public static T Deserialize<T>(string json)
        {
            return Make().Deserialize<T>(json);
        }

        /// <summary>Indents compact JSON so the settings file stays human-editable.</summary>
        public static string Pretty(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            var sb = new StringBuilder(json.Length * 2);
            int indent = 0;
            bool inStr = false, esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(json[i + 1]);
                            i++;
                            break;
                        }
                        indent++;
                        NewLine(sb, indent);
                        break;
                    case '}':
                    case ']':
                        indent--;
                        NewLine(sb, indent);
                        sb.Append(c);
                        break;
                    case ',': sb.Append(c); NewLine(sb, indent); break;
                    case ':': sb.Append(": "); break;
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n': break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append("\r\n");
            sb.Append(' ', indent * 2);
        }
    }
}
