namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// M32/X32 effect types that carry a delay time and can therefore be driven by
/// the <see cref="Modules.DelayController"/>. The engineer selects, per FX slot,
/// the effect actually loaded on the console so the time is written to the right
/// parameter (it differs between effects).
/// </summary>
public enum DelayEffectType
{
    /// <summary>Stereo Delay (DLY). Time is on parameter 2 (parameter 1 is Mix).</summary>
    StereoDelay,

    /// <summary>3-Tap Delay (3TAP). Time is on parameter 1.</summary>
    ThreeTapDelay,

    /// <summary>4-Tap Delay (4TAP). Time is on parameter 1.</summary>
    FourTapDelay,

    /// <summary>Delay + Chamber (D/RV). Time is on parameter 1.</summary>
    DelayChamber,

    /// <summary>Delay + Chorus (D/CR). Time is on parameter 1.</summary>
    DelayChorus,

    /// <summary>Delay + Flanger (D/FL). Time is on parameter 1.</summary>
    DelayFlanger,

    /// <summary>Modulation Delay (MODD). Time is on parameter 1.</summary>
    ModulationDelay
}

/// <summary>
/// Describes where each <see cref="DelayEffectType"/> keeps its delay time. Every
/// M32 delay effect uses a 1-3000 ms range; only the Stereo Delay puts the time
/// on parameter 2 (its parameter 1 is Mix), the rest use parameter 1.
/// </summary>
public static class DelayEffects
{
    /// <summary>Minimum delay time in milliseconds (shared by all delay effects).</summary>
    public const double TIME_MIN_MS = 1.0;

    /// <summary>Maximum delay time in milliseconds (shared by all delay effects).</summary>
    public const double TIME_MAX_MS = 3000.0;

    /// <summary>
    /// 1-based FX parameter number that holds the delay time for the given effect.
    /// </summary>
    public static int TimeParameterNumber(DelayEffectType type)
    {
        return type == DelayEffectType.StereoDelay ? 2 : 1;
    }

    /// <summary>
    /// Clamps a delay time to the range every M32 delay effect supports
    /// (<see cref="TIME_MIN_MS"/>..<see cref="TIME_MAX_MS"/>). The console saturates a
    /// longer time silently, so the UI uses this to show the value the console
    /// actually receives instead of an unreachable theoretical one (e.g. a whole
    /// note at a slow tempo can exceed 3000 ms).
    /// </summary>
    public static double ClampTimeMs(double timeMs)
    {
        return Math.Clamp(timeMs, TIME_MIN_MS, TIME_MAX_MS);
    }
}
