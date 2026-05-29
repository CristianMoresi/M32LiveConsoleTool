using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for the delay-slot configuration value object and the delay-effect
/// time-parameter mapping.
/// </summary>
public class DelaySlotConfigTests
{
    [Fact]
    public void Automatic_recomputes_from_bpm()
    {
        DelaySlotConfig config = DelaySlotConfig.Automatic(DelayEffectType.StereoDelay, BeatType.Quarter);

        Assert.True(config.IsAutomatic);
        Assert.Equal(DelayEffectType.StereoDelay, config.EffectType);
        Assert.Equal(500.0, config.GetDelayMs(120.0), 6);
        Assert.Equal(1000.0, config.GetDelayMs(60.0), 6);
    }

    [Fact]
    public void Automatic_returns_zero_without_a_valid_bpm()
    {
        DelaySlotConfig config = DelaySlotConfig.Automatic(DelayEffectType.ThreeTapDelay, BeatType.Eighth);

        Assert.Equal(0.0, config.GetDelayMs(0.0), 6);
    }

    [Fact]
    public void Manual_ignores_bpm_and_keeps_its_fixed_time()
    {
        DelaySlotConfig config = DelaySlotConfig.Manual(DelayEffectType.StereoDelay, 80.0);

        Assert.False(config.IsAutomatic);
        Assert.Equal(80.0, config.GetDelayMs(120.0), 6);
        Assert.Equal(80.0, config.GetDelayMs(0.0), 6);
    }

    [Fact]
    public void Manual_rejects_non_positive_times()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DelaySlotConfig.Manual(DelayEffectType.StereoDelay, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DelaySlotConfig.Manual(DelayEffectType.StereoDelay, -50.0));
    }

    [Fact]
    public void Time_parameter_number_matches_the_protocol()
    {
        // Only the Stereo Delay keeps the time on parameter 2; the rest use 1.
        Assert.Equal(2, DelayEffects.TimeParameterNumber(DelayEffectType.StereoDelay));
        Assert.Equal(1, DelayEffects.TimeParameterNumber(DelayEffectType.ThreeTapDelay));
        Assert.Equal(1, DelayEffects.TimeParameterNumber(DelayEffectType.FourTapDelay));
        Assert.Equal(1, DelayEffects.TimeParameterNumber(DelayEffectType.DelayChamber));
        Assert.Equal(1, DelayEffects.TimeParameterNumber(DelayEffectType.DelayChorus));
        Assert.Equal(1, DelayEffects.TimeParameterNumber(DelayEffectType.DelayFlanger));
        Assert.Equal(1, DelayEffects.TimeParameterNumber(DelayEffectType.ModulationDelay));
    }
}
