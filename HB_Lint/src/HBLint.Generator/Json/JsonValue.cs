using System.Collections.Generic;

namespace HBLint.Generator.Json
{
    /// <summary>JSON 값. Line · Column은 원문 위치(1부터, 만든 값이면 0)로 오류 메시지에 쓴다.</summary>
    public abstract class JsonValue
    {
        /// <summary>종류는 아래 여섯 개로 닫는다.</summary>
        internal JsonValue()
        {
        }

        public int Line { get; internal set; }

        public int Column { get; internal set; }

        public abstract string KindName { get; }
    }

    public sealed class JsonObject : JsonValue
    {
        private readonly List<KeyValuePair<string, JsonValue>> _members = new List<KeyValuePair<string, JsonValue>>();

        /// <summary>원문 순서 그대로.</summary>
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members => _members;

        public override string KindName => "객체";

        public JsonValue? Get(string name)
        {
            foreach (var member in _members)
            {
                if (member.Key == name)
                    return member.Value;
            }
            return null;
        }

        public bool Contains(string name) => Get(name) != null;

        public JsonObject Add(string name, JsonValue value)
        {
            _members.Add(new KeyValuePair<string, JsonValue>(name, value));
            return this;
        }
    }

    public sealed class JsonArray : JsonValue
    {
        private readonly List<JsonValue> _items = new List<JsonValue>();

        public IReadOnlyList<JsonValue> Items => _items;

        public override string KindName => "배열";

        public JsonArray Add(JsonValue value)
        {
            _items.Add(value);
            return this;
        }
    }

    public sealed class JsonString : JsonValue
    {
        public JsonString(string value) => Value = value;

        public string Value { get; }

        public override string KindName => "문자열";
    }

    /// <summary>숫자는 원문 그대로 둔다 (정수 · 범위 판단은 쓰는 쪽).</summary>
    public sealed class JsonNumber : JsonValue
    {
        public JsonNumber(string raw) => Raw = raw;

        public string Raw { get; }

        public override string KindName => "숫자";
    }

    public sealed class JsonBool : JsonValue
    {
        public JsonBool(bool value) => Value = value;

        public bool Value { get; }

        public override string KindName => "bool";
    }

    public sealed class JsonNull : JsonValue
    {
        public override string KindName => "null";
    }
}
