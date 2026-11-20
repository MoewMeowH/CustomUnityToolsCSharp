using HBLint.Core.Json;

namespace HBLint.Core.Tests;

public class JsonReaderTests
{
    [Fact]
    public void 객체_배열_값_종류와_칸_순서()
    {
        var value = JsonReader.Parse("""{ "b": [1, -2.5e3, true, false, null], "a": "x" }""");

        var obj = Assert.IsType<JsonObject>(value);
        Assert.Equal(new[] { "b", "a" }, obj.Members.Select(m => m.Key));
        var array = Assert.IsType<JsonArray>(obj.Get("b"));
        Assert.Equal("1", Assert.IsType<JsonNumber>(array.Items[0]).Raw);
        Assert.Equal("-2.5e3", Assert.IsType<JsonNumber>(array.Items[1]).Raw);
        Assert.True(Assert.IsType<JsonBool>(array.Items[2]).Value);
        Assert.False(Assert.IsType<JsonBool>(array.Items[3]).Value);
        Assert.IsType<JsonNull>(array.Items[4]);
        Assert.Equal("x", Assert.IsType<JsonString>(obj.Get("a")).Value);
    }

    [Fact]
    public void 문자열_이스케이프와_대리_쌍()
    {
        const char Backslash = '\\';
        // "\"\\/\b\f\n\r\t" + A(\u0041) + 한(\uD55C) + 😀(\uD83D\uDE00)
        var json = "\"" + Backslash + "\"" + Backslash + Backslash + Backslash + "/"
                   + Backslash + "b" + Backslash + "f" + Backslash + "n" + Backslash + "r" + Backslash + "t"
                   + Backslash + "u0041" + Backslash + "uD55C" + Backslash + "uD83D" + Backslash + "uDE00" + "\"";

        var value = Assert.IsType<JsonString>(JsonReader.Parse(json)).Value;

        Assert.Equal("\"\\/\b\f\n\r\tA" + (char)0xD55C + char.ConvertFromUtf32(0x1F600), value);
    }

    [Fact]
    public void 맨_앞_BOM은_건너뜀()
    {
        Assert.IsType<JsonObject>(JsonReader.Parse((char)0xFEFF + "{}"));
    }

    [Fact]
    public void 값의_줄과_칸을_기록()
    {
        var obj = (JsonObject)JsonReader.Parse("{\n  \"a\":\n    [1,\n  \"x\"]\n}");

        var array = obj.Get("a")!;
        Assert.Equal((3, 5), (array.Line, array.Column));
        var second = ((JsonArray)array).Items[1];
        Assert.Equal((4, 3), (second.Line, second.Column));
    }

    [Theory]
    [InlineData("{\"a\": 1,}", 1, 9)]               // 끝 쉼표
    [InlineData("[1, 2,]", 1, 7)]
    [InlineData("{\"a\": 1 \"b\": 2}", 1, 9)]       // 쉼표 없음
    [InlineData("{\"a\": 1, \"a\": 2}", 1, 10)]     // 이름 중복
    [InlineData("// 주석\n{}", 1, 1)]
    [InlineData("{} x", 1, 4)]                      // 뒤에 남은 글자
    [InlineData("\"abc", 1, 1)]                     // 안 닫힌 문자열
    [InlineData("\"a\tb\"", 1, 3)]                  // 이스케이프 안 한 탭
    [InlineData("\"\\x\"", 1, 2)]                   // 모르는 이스케이프
    [InlineData("\"\\u12\"", 1, 2)]                 // \u 자리 모자람
    [InlineData("01", 1, 2)]                        // 앞자리 0
    [InlineData("1.", 1, 1)]
    [InlineData("-", 1, 1)]
    [InlineData("tru", 1, 1)]
    [InlineData("", 1, 1)]
    [InlineData("{\n  \"a\": ?\n}", 2, 8)]
    public void 형식_오류는_줄과_칸과_함께(string json, int line, int column)
    {
        var e = Assert.Throws<JsonParseException>(() => JsonReader.Parse(json));

        Assert.Equal((line, column), (e.Line, e.Column));
    }

    [Fact]
    public void 중첩_깊이_상한()
    {
        var ok = new string('[', JsonReader.MaxDepth) + new string(']', JsonReader.MaxDepth);
        var tooDeep = new string('[', JsonReader.MaxDepth + 1) + new string(']', JsonReader.MaxDepth + 1);

        JsonReader.Parse(ok);
        Assert.Throws<JsonParseException>(() => JsonReader.Parse(tooDeep));
    }

    [Fact]
    public void 쓰고_다시_읽으면_같음()
    {
        var source = new JsonObject()
            .Add("s", new JsonString("따옴표\" 역슬래시\\ 줄\n탭\t제어" + (char)1))
            .Add("a", new JsonArray().Add(new JsonNumber("12")).Add(new JsonBool(true)).Add(new JsonNull()))
            .Add("e", new JsonObject())
            .Add("ea", new JsonArray());

        var text = JsonWriter.Write(source);
        var back = (JsonObject)JsonReader.Parse(text);

        Assert.Equal(text, JsonWriter.Write(back));
        Assert.Equal("따옴표\" 역슬래시\\ 줄\n탭\t제어" + (char)1, ((JsonString)back.Get("s")!).Value);
        Assert.DoesNotContain("\r", text);
    }
}
