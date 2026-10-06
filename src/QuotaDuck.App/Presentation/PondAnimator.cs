using QuotaDuck.Art;

namespace QuotaDuck.App.Presentation;

/// <summary>
/// The only animation that changes WHAT is drawn: a short refill (8 steps) when a pond's water rises substantially — e.g. a window that was
/// elapsed/hatched becomes a real level, or the quota resets. Pure and step-driven: the caller owns the (short-lived, visible-only) timer.
/// No animation is needed to understand any state; under reduced motion <see cref="Observe"/> never starts one.
/// </summary>
public sealed class PondAnimator
{
    public const int Steps = 8;
    public const double MinRise = 0.25;

    private PondScene? _previous;
    private int _step = Steps;
    private double _from, _to;

    public bool Active => _step < Steps;

    /// <summary>Feed the latest target scene. Returns true while a tween is running.</summary>
    public bool Observe(PondScene target, bool allowAnimation)
    {
        PondScene? prev = _previous;
        _previous = target;
        if (!allowAnimation)
        {
            _step = Steps;
            return false;
        }

        if (WaterLevel(target) is not { } to)
        {
            _step = Steps;
            return false;
        }

        if (Active)
        {
            _to = to;                                                          // retarget a running refill; never restart it
            return true;
        }

        if (prev is null)
        {
            return false;                                                      // first observation: nothing to animate from
        }

        double from = WaterLevel(prev) ?? 0;
        if (to - from >= MinRise)
        {
            (_from, _to, _step) = (from, to, 0);
            return true;
        }

        return false;
    }

    /// <summary>Advances one step; returns true while still running.</summary>
    public bool Step()
    {
        if (Active)
        {
            _step++;
        }

        return Active;
    }

    /// <summary>The scene to draw now: the target, with its water level interpolated while a refill runs.</summary>
    public PondScene Current(PondScene target) =>
        Active && WaterLevel(target) is not null
            ? target with { Remaining = _from + (_to - _from) * _step / Steps }
            : target;

    private static double? WaterLevel(PondScene s) =>
        s.Condition is PondCondition.Normal or PondCondition.Subdued ? s.Remaining : null;
}
