using System.Runtime.CompilerServices;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>시험 어셈블리 전체 설정 — 시험이 시작되기 전에 한 번.</summary>
internal static class TestSetup
{
    /// <summary>
    /// 쉬는 실행: 작업 없는 턴은 result에서 바로 닫는다 (IdleGrace 0) — 말을 연달아 보내는 시험이 이어 쓰기 여부로 흔들리지 않게(예전 한 실행 한 턴과 같게).
    /// 쉬는 동작을 보는 시험은 방 허브 · 세션에 시간 값을 따로 넘긴다.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => ClaudeSession.DefaultTimings = new() { IdleGrace = TimeSpan.Zero };
}
