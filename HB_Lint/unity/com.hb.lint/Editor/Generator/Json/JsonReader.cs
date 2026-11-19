#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HBLint.Generator.Json
{
    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message, int line, int column)
            : base(message)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }

        public int Column { get; }
    }

    /// <summary>RFC 8259 JSON 읽기 (주석 · 끝 쉼표 없음). 같은 객체 안 이름 중복은 오류. 맨 앞 BOM은 건너뛴다.</summary>
    public sealed class JsonReader
    {
        public const int MaxDepth = 64;

        private readonly string _text;
        private readonly List<int> _lineStarts = new List<int> { 0 };
        private int _pos;
        private int _depth;

        private JsonReader(string text)
        {
            _text = text;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    _lineStarts.Add(i + 1);
            }
            if (text.Length > 0 && text[0] == '﻿')
                _pos = 1;
        }

        public static JsonValue Parse(string text)
        {
            var reader = new JsonReader(text);
            reader.SkipWhitespace();
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (reader._pos < text.Length)
                throw reader.Error("값 뒤에 남은 글자가 있습니다", reader._pos);
            return value;
        }

        private JsonValue ReadValue()
        {
            if (_pos >= _text.Length)
                throw Error("값이 와야 하는데 글이 끝났습니다", _pos);

            var start = _pos;
            JsonValue value;
            switch (_text[_pos])
            {
                case '{': value = ReadObject(); break;
                case '[': value = ReadArray(); break;
                case '"': value = new JsonString(ReadString()); break;
                case 't': ReadLiteral("true"); value = new JsonBool(true); break;
                case 'f': ReadLiteral("false"); value = new JsonBool(false); break;
                case 'n': ReadLiteral("null"); value = new JsonNull(); break;
                default:
                    if (_text[_pos] == '-' || IsDigit(_text[_pos]))
                    {
                        value = new JsonNumber(ReadNumber());
                        break;
                    }
                    throw Error($"알 수 없는 글자 '{Describe(_text[_pos])}'", _pos);
            }
            SetPosition(value, start);
            return value;
        }

        private JsonObject ReadObject()
        {
            EnterNesting();
            var obj = new JsonObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            _pos++; // {
            SkipWhitespace();
            if (Peek() == '}')
            {
                _pos++;
                _depth--;
                return obj;
            }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                    throw Error("칸 이름(문자열)이 와야 합니다", _pos);
                var nameStart = _pos;
                var name = ReadString();
                if (!names.Add(name))
                    throw Error($"칸 이름 '{name}'이(가) 같은 객체에 두 번 있습니다", nameStart);
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                obj.Add(name, ReadValue());
                SkipWhitespace();
                if (Peek() == ',')
                {
                    _pos++;
                    continue;
                }
                Expect('}');
                _depth--;
                return obj;
            }
        }

        private JsonArray ReadArray()
        {
            EnterNesting();
            var array = new JsonArray();
            _pos++; // [
            SkipWhitespace();
            if (Peek() == ']')
            {
                _pos++;
                _depth--;
                return array;
            }
            while (true)
            {
                SkipWhitespace();
                array.Add(ReadValue());
                SkipWhitespace();
                if (Peek() == ',')
                {
                    _pos++;
                    continue;
                }
                Expect(']');
                _depth--;
                return array;
            }
        }

        private string ReadString()
        {
            var start = _pos;
            _pos++; // "
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length)
                    throw Error("문자열이 닫히지 않았습니다", start);
                var c = _text[_pos];
                if (c == '"')
                {
                    _pos++;
                    return sb.ToString();
                }
                if (c < 0x20)
                    throw Error($"문자열 안에 이스케이프하지 않은 제어 문자 '{Describe(c)}'가 있습니다", _pos);
                if (c != '\\')
                {
                    sb.Append(c);
                    _pos++;
                    continue;
                }

                var escapeStart = _pos;
                _pos++;
                if (_pos >= _text.Length)
                    throw Error("문자열이 닫히지 않았습니다", start);
                switch (_text[_pos])
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
                        if (_pos + 4 >= _text.Length
                            || !int.TryParse(_text.Substring(_pos + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw Error("\\u 뒤에는 16진수 네 자리가 와야 합니다", escapeStart);
                        sb.Append((char)code);
                        _pos += 4;
                        break;
                    default:
                        throw Error($"알 수 없는 이스케이프 '\\{Describe(_text[_pos])}'", escapeStart);
                }
                _pos++;
            }
        }

        private string ReadNumber()
        {
            var start = _pos;
            if (Peek() == '-')
                _pos++;
            if (Peek() == '0')
            {
                _pos++;
            }
            else if (IsDigit(Peek()))
            {
                while (IsDigit(Peek()))
                    _pos++;
            }
            else
            {
                throw Error("숫자 형식이 틀렸습니다", start);
            }
            if (Peek() == '.')
            {
                _pos++;
                if (!IsDigit(Peek()))
                    throw Error("소수점 뒤에 숫자가 와야 합니다", start);
                while (IsDigit(Peek()))
                    _pos++;
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                _pos++;
                if (Peek() == '+' || Peek() == '-')
                    _pos++;
                if (!IsDigit(Peek()))
                    throw Error("지수 뒤에 숫자가 와야 합니다", start);
                while (IsDigit(Peek()))
                    _pos++;
            }
            return _text.Substring(start, _pos - start);
        }

        private void ReadLiteral(string literal)
        {
            if (string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
                throw Error($"'{literal}'을(를) 기대했습니다", _pos);
            _pos += literal.Length;
        }

        private void EnterNesting()
        {
            _depth++;
            if (_depth > MaxDepth)
                throw Error($"중첩이 {MaxDepth}단을 넘습니다", _pos);
        }

        private void Expect(char c)
        {
            if (Peek() != c)
                throw Error(_pos >= _text.Length ? $"'{c}'이(가) 와야 하는데 글이 끝났습니다" : $"'{c}'이(가) 와야 합니다", _pos);
            _pos++;
        }

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private void SkipWhitespace()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    return;
                _pos++;
            }
        }

        private void SetPosition(JsonValue value, int offset)
        {
            var (line, column) = Position(offset);
            value.Line = line;
            value.Column = column;
        }

        private (int Line, int Column) Position(int offset)
        {
            var index = _lineStarts.BinarySearch(offset);
            if (index < 0)
                index = ~index - 1;
            return (index + 1, offset - _lineStarts[index] + 1);
        }

        private JsonParseException Error(string message, int offset)
        {
            var (line, column) = Position(offset);
            return new JsonParseException(message, line, column);
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static string Describe(char c) => c < 0x20 ? $"U+{(int)c:X4}" : c.ToString();
    }
}
