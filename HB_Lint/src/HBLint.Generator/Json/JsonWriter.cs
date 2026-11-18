using System.Text;

namespace HBLint.Generator.Json
{
    /// <summary>들여쓰기 2칸 · LF · 칸 순서 그대로. ASCII 밖 글자는 그대로 둔다 (파일은 UTF-8).</summary>
    public static class JsonWriter
    {
        public static string Write(JsonValue value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue value, int indent)
        {
            switch (value)
            {
                case JsonObject obj:
                    if (obj.Members.Count == 0)
                    {
                        sb.Append("{}");
                        return;
                    }
                    sb.Append("{\n");
                    for (var i = 0; i < obj.Members.Count; i++)
                    {
                        Indent(sb, indent + 1);
                        WriteString(sb, obj.Members[i].Key);
                        sb.Append(": ");
                        WriteValue(sb, obj.Members[i].Value, indent + 1);
                        sb.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
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
                    sb.Append("[\n");
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        Indent(sb, indent + 1);
                        WriteValue(sb, array.Items[i], indent + 1);
                        sb.Append(i + 1 < array.Items.Count ? ",\n" : "\n");
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

        private static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);
    }
}
