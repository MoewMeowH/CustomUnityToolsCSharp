#nullable enable
using System.Text;

namespace HBLint.Core.Json
{
    /// <summary>들여쓰기 2칸 · LF · 칸 순서 그대로. ASCII 밖 글자는 그대로 둔다 (파일은 UTF-8). Compact는 공백 · 줄바꿈 없이.</summary>
    internal static class JsonWriter
    {
        public static string Write(JsonValue value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        public static string WriteCompact(JsonValue value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, -1);
            return sb.ToString();
        }

        /// <summary>indent가 음수면 한 줄로 쓴다.</summary>
        private static void WriteValue(StringBuilder sb, JsonValue value, int indent)
        {
            var compact = indent < 0;
            var inner = compact ? -1 : indent + 1;
            switch (value)
            {
                case JsonObject obj:
                    if (obj.Members.Count == 0)
                    {
                        sb.Append("{}");
                        return;
                    }
                    sb.Append(compact ? "{" : "{\n");
                    for (var i = 0; i < obj.Members.Count; i++)
                    {
                        Indent(sb, inner);
                        WriteString(sb, obj.Members[i].Key);
                        sb.Append(compact ? ":" : ": ");
                        WriteValue(sb, obj.Members[i].Value, inner);
                        sb.Append(i + 1 < obj.Members.Count ? (compact ? "," : ",\n") : (compact ? "" : "\n"));
                    }
                    Indent(sb, indent);
                    sb.Append('}');
                    return;
                case JsonArray array:
                    if (array.Items.Count == 0)
                    {
                        sb.Append("[]");
                        return;
                    }
                    sb.Append(compact ? "[" : "[\n");
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        Indent(sb, inner);
                        WriteValue(sb, array.Items[i], inner);
                        sb.Append(i + 1 < array.Items.Count ? (compact ? "," : ",\n") : (compact ? "" : "\n"));
                    }
                    Indent(sb, indent);
                    sb.Append(']');
                    return;
                case JsonString str:
                    WriteString(sb, str.Value);
                    return;
                case JsonNumber number:
                    sb.Append(number.Raw);
                    return;
                case JsonBool boolean:
                    sb.Append(boolean.Value ? "true" : "false");
                    return;
                case JsonNull _:
                    sb.Append("null");
                    return;
            }
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
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
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int level)
        {
            if (level > 0)
                sb.Append(' ', level * 2);
        }
    }
}
