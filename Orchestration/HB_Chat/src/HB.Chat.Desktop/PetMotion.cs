namespace HB.Chat.Desktop;

/// <summary>한 틱의 결과: 새 가로 자리(옮길 때만) · 걷기가 끝났는지(도착 · 멈춤 — 자리를 저장하라고).</summary>
public readonly record struct PetStep(int? X, bool Ended);

/// <summary>
/// 쉼 때 바닥 걷기 (결정 "쉼 때 바닥 걸기"): 걸어도 되는 동안 8~20초 쉬었다가, 펫이 있는 화면 작업 영역 안에서 120px 이상 떨어진 가로 자리로
/// 초당 펫 너비 × 0.6만큼 걷는다 (세로는 그대로 — 끌어 놓은 높이). 걸어도 되지 않게 되면 바로 멈춘다.
/// 시계 · 난수를 받아 창 없이 시험한다. UI 스레드에서만 쓴다.
/// </summary>
public sealed class PetMotion(Func<DateTime> now, Random random)
{
    public static readonly TimeSpan RestMin = TimeSpan.FromSeconds(8), RestMax = TimeSpan.FromSeconds(20);
    public const int MinStep = 120;
    public const double SpeedPerWidth = 0.6;

    /// <summary>한 틱에 쳐 주는 시간 상한 — 창이 멈췄다 풀려도 한 번에 멀리 뛰지 않게.</summary>
    private static readonly TimeSpan MaxTick = TimeSpan.FromMilliseconds(100);

    private DateTime? _restUntil;
    private int? _target;
    private double _x;
    private int _shown;
    private DateTime _last;

    public bool Walking => _target is not null;

    /// <summary>걷는 방향 그림 (걷지 않으면 null).</summary>
    public PetAction? Facing => _target is { } t ? t >= _x ? PetAction.RunRight : PetAction.RunLeft : null;

    /// <param name="allowed">지금 걸어도 되는지 (움직이기 켬 · 쉼 · 한 번 동작 없음 · 누르지 않음 · 보임 · 연결됨 · 달리기 그림 있음).</param>
    /// <param name="pet">지금 펫 창 자리.</param>
    /// <param name="work">펫이 걸친 화면의 작업 영역.</param>
    public PetStep Tick(bool allowed, Rectangle pet, Rectangle work)
    {
        if (!allowed) return new(null, Stop());
        var t = now();
        if (_target is null)
        {
            _restUntil ??= t + Rest();
            if (t < _restUntil) return default;
            _restUntil = null;
            _target = Target(pet, work, random);
            if (_target is null)
            {
                _restUntil = t + Rest(); // 걸을 자리가 없다 (좁은 화면) — 다음에 다시
                return default;
            }
            _x = pet.Left;
            _shown = pet.Left;
            _last = t;
        }
        // 마지막으로 낸 자리와 다르면 바깥에서 옮겨졌다(크기 바꾸기 · 화면 구성 변경) — 걷던 _x로 되돌아가지 않고 멈춘다
        if (pet.Left != _shown) return new(null, Stop());
        var target = Math.Clamp(_target.Value, work.Left, Math.Max(work.Left, work.Right - pet.Width)); // 화면이 바뀌었으면 그 안으로
        _target = target;
        var dt = t - _last;
        _last = t;
        var step = pet.Width * SpeedPerWidth * Math.Clamp(dt.TotalSeconds, 0, MaxTick.TotalSeconds);
        _x = target > _x ? Math.Min(target, _x + step) : Math.Max(target, _x - step);
        var x = (int)Math.Round(_x);
        if (Math.Abs(_x - target) < 0.5)
        {
            _target = null;
            _restUntil = t + Rest();
            return new(target, true);
        }
        _shown = x; // 부른 쪽이 이 자리로 옮긴다
        return new(x == pet.Left ? null : x, false);
    }

    /// <summary>걷기를 멈추고 쉼 시간을 처음부터. 걷던 중이었으면 true (자리를 저장하라고).</summary>
    public bool Stop()
    {
        var was = _target is not null;
        _target = null;
        _restUntil = null;
        return was;
    }

    private TimeSpan Rest() => RestMin + (RestMax - RestMin) * random.NextDouble();

    /// <summary>
    /// 걸어갈 가로 자리: 작업 영역 안(펫 너비 포함)에서 지금 자리와 <see cref="MinStep"/> 이상 떨어진 곳을 고르게. 그런 곳이 없으면 null.
    /// </summary>
    public static int? Target(Rectangle pet, Rectangle work, Random random)
    {
        int lo = work.Left, hi = work.Right - pet.Width, x = Math.Clamp(pet.Left, lo, Math.Max(lo, hi));
        if (hi < lo) return null;
        int leftEnd = x - MinStep, rightStart = x + MinStep;
        int leftLen = Math.Max(0, leftEnd - lo + 1), rightLen = Math.Max(0, hi - rightStart + 1);
        if (leftLen + rightLen == 0) return null;
        var pick = random.Next(leftLen + rightLen);
        return pick < leftLen ? lo + pick : rightStart + (pick - leftLen);
    }
}
