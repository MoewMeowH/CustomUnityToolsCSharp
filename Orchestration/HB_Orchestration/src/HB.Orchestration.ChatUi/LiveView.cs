namespace HB.Orchestration.ChatUi;

/// <summary>실시간 갱신 설정 하나 (_LiveControl · _LiveScript). Stamp는 화면을 그리기 전에 잡은 값.</summary>
public sealed record LiveView(string Stamp, string StampUrl, string ContentUrl, string TargetId, int Seconds, bool Follow);
