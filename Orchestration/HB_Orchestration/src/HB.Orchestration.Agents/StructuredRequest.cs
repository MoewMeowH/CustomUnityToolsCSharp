using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace HB.Orchestration.Agents;

/// <summary>
/// 구조화 출력 Schema 방식의 요청 옵션. strict 여부를 MEAI 변환 규칙(추가 속성 키)에 맡기지 않고
/// OpenAI SDK 옵션을 직접 만들어 <c>response_format: {type: json_schema, strict: true}</c>를 확정한다.
/// ChatOptions.ResponseFormat은 비워 둔다 (채우면 MEAI가 이 값을 덮어쓴다).
/// </summary>
public static class StructuredRequest
{
    public static ChatOptions Options(string instructions, FindingFormat format)
    {
        var schema = BinaryData.FromString(format.Schema().GetRawText());
        return new ChatOptions
        {
            Instructions = instructions,
            RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(format.Name, schema, jsonSchemaIsStrict: true),
            },
        };
    }
}
