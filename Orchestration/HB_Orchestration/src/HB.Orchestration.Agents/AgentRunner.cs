using System.Diagnostics;
using HB.Orchestration.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace HB.Orchestration.Agents;

/// <summary>기록용 정보. Files는 에이전트에 안 보내는 링크, Context는 실제로 첨부한 코드 범위.</summary>
public sealed record CallMeta(IReadOnlyList<string>? Files, IReadOnlyList<string> Context);

/// <summary>모드의 한 단계 실행 정보.</summary>
public sealed record StepCall(
    string RunId, string Mode, int Step, string Role,
    AgentEntry Agent, string Instructions, string Prompt,
    CallMeta Meta);

public sealed record StepResult(int Step, string Role, string Agent, bool Success, string Text);

/// <summary>에이전트 한 번 실행 + 호출 기록. 실행은 전부 여기를 거친다.</summary>
public sealed class AgentRunner(ILogger<AgentRunner> logger)
{
    public async Task<StepResult> RunStepAsync(StepCall call, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        string text;
        CallRecord record;

        try
        {
            // 역할 프롬프트는 단계마다 다르므로 실행 시점에 MAF 에이전트를 만든다 (가벼운 객체).
            AIAgent agent = new ChatClientAgent(call.Agent.Client, instructions: call.Instructions, name: call.Agent.Name);

            // 호출마다 새 스레드: 이전 대화가 섞이지 않는다 (= 쓸데없는 히스토리 토큰 없음).
            var response = await agent.RunAsync(call.Prompt, cancellationToken: ct);
            text = response.Text;
            var inTokens = response.Usage?.InputTokenCount;
            var outTokens = response.Usage?.OutputTokenCount;

            if (string.IsNullOrWhiteSpace(text))
            {
                // 추론 모델은 생각 토큰도 MaxOutputTokens에 포함된다. 상한을 다 쓰면 답변 없이 "성공"으로 끝나므로
                // Claude가 "문제 없음"으로 오해하지 않도록 실패로 돌려준다.
                var max = call.Agent.Options.MaxOutputTokens;
                text = max is { } m && outTokens >= m * 0.95
                    ? $"[빈 응답] 출력 토큰 상한({m:N0})을 추론에 다 써서 답변을 못 했습니다. appsettings.json에서 '{call.Agent.Name}'의 MaxOutputTokens를 올리세요."
                    : $"[빈 응답] 에이전트가 내용 없이 응답했습니다. (출력 토큰 {outTokens?.ToString("N0") ?? "-"})";

                record = NewRecord() with
                {
                    InputTokens = inTokens,
                    OutputTokens = outTokens,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Success = false,
                    Error = text,
                };
                logger.LogWarning("[{Mode}#{Step}] {Role}@{Agent} 빈 응답 (출력 {Out})", call.Mode, call.Step, call.Role, call.Agent.Name, outTokens);
            }
            else
            {
                record = NewRecord() with
                {
                    Response = text,
                    InputTokens = inTokens,
                    OutputTokens = outTokens,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Success = true,
                };
            }

            logger.LogInformation("[{Mode}#{Step}] {Role}@{Agent} 완료 {Elapsed}ms, 입력 {In} / 출력 {Out}",
                call.Mode, call.Step, call.Role, call.Agent.Name, record.ElapsedMs, record.InputTokens, record.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Mode}#{Step}] {Role}@{Agent} 실패", call.Mode, call.Step, call.Role, call.Agent.Name);
            text = $"[실패] {ex.GetType().Name}: {ex.Message}";
            record = NewRecord() with { ElapsedMs = sw.ElapsedMilliseconds, Success = false, Error = text };
        }

        // 기록 실패가 결과 전달을 막으면 안 된다.
        try { await CallLog.AppendAsync(record); }
        catch (Exception ex) { logger.LogWarning(ex, "호출 기록 저장 실패"); }

        return new StepResult(call.Step, call.Role, call.Agent.Name, record.Success, text);

        CallRecord NewRecord() => new()
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Timestamp = startedAt,
            Tool = call.Mode,
            RunId = call.RunId,
            Mode = call.Mode,
            Role = call.Role,
            Step = call.Step,
            Agent = call.Agent.Name,
            Provider = call.Agent.Provider,
            Model = call.Agent.Model,
            ProjectDir = Environment.CurrentDirectory,
            Prompt = call.Prompt,
            Files = call.Meta.Files ?? [],
            Context = call.Meta.Context,
        };
    }
}
