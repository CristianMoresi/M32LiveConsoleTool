using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for the fader/dB/meter conversions and the beat-to-milliseconds math.
/// </summary>
public class FaderMathTests
{
    [Fact]
    public void FaderToDb_hits_the_known_anchor_points()
    {
        // X32/M32 four-segment taper: unity (0 dB) sits at 0.75.
        Assert.True(double.IsNegativeInfinity(FaderMath.FaderToDb(0f)));
        Assert.Equal(-60.0, FaderMath.FaderToDb(0.0625f), 5);
        Assert.Equal(-30.0, FaderMath.FaderToDb(0.25f), 5);
        Assert.Equal(-10.0, FaderMath.FaderToDb(0.5f), 5);
        Assert.Equal(0.0, FaderMath.FaderToDb(0.75f), 5);
        Assert.Equal(10.0, FaderMath.FaderToDb(1f), 5);
    }

    [Fact]
    public void DbToFader_hits_the_known_anchor_points()
    {
        Assert.Equal(0f, FaderMath.DbToFader(-90.0), 5);
        Assert.Equal(0.0625f, FaderMath.DbToFader(-60.0), 5);
        Assert.Equal(0.25f, FaderMath.DbToFader(-30.0), 5);
        Assert.Equal(0.5f, FaderMath.DbToFader(-10.0), 5);
        Assert.Equal(0.75f, FaderMath.DbToFader(0.0), 5);
        Assert.Equal(1f, FaderMath.DbToFader(10.0), 5);
    }

    [Theory]
    [InlineData(0.04f)]    // lowest segment
    [InlineData(0.0625f)]  // segment boundary
    [InlineData(0.1f)]
    [InlineData(0.25f)]    // segment boundary
    [InlineData(0.4f)]
    [InlineData(0.5f)]     // segment boundary
    [InlineData(0.75f)]    // unity
    [InlineData(0.9f)]
    public void FaderToDb_and_DbToFader_round_trip(float fader)
    {
        double db = FaderMath.FaderToDb(fader);
        float back = FaderMath.DbToFader(db);

        Assert.Equal(fader, back, 4);
    }

    [Fact]
    public void MeterToDb_clamps_very_low_values_to_the_floor()
    {
        Assert.Equal(-90.0, FaderMath.MeterToDb(0f), 6);
        Assert.Equal(-90.0, FaderMath.MeterToDb(1e-9f), 6);
    }

    [Fact]
    public void MeterToDb_of_unity_is_zero()
    {
        Assert.Equal(0.0, FaderMath.MeterToDb(1f), 6);
    }

    [Theory]
    [InlineData(120.0, BeatType.Quarter, 500.0)]
    [InlineData(120.0, BeatType.Eighth, 250.0)]
    [InlineData(120.0, BeatType.Half, 1000.0)]
    [InlineData(60.0, BeatType.Quarter, 1000.0)]
    [InlineData(120.0, BeatType.DottedQuarter, 750.0)]
    public void DelayMsForBeat_computes_known_times(double bpm, BeatType beat, double expectedMs)
    {
        Assert.Equal(expectedMs, FaderMath.DelayMsForBeat(bpm, beat), 6);
    }

    [Fact]
    public void DelayMsForBeat_returns_zero_for_non_positive_bpm()
    {
        Assert.Equal(0.0, FaderMath.DelayMsForBeat(0.0, BeatType.Quarter), 6);
        Assert.Equal(0.0, FaderMath.DelayMsForBeat(-120.0, BeatType.Quarter), 6);
    }

    [Theory]
    [InlineData(-12.0, -12.0, 60.0, 0.0)]   // head amp min -> 0
    [InlineData(60.0, -12.0, 60.0, 1.0)]    // head amp max -> 1
    [InlineData(24.0, -12.0, 60.0, 0.5)]    // mid -> 0.5
    [InlineData(0.0, -18.0, 18.0, 0.5)]     // trim 0 dB -> 0.5
    public void Normalize_maps_physical_range_to_unit(double value, double min, double max, double expected)
    {
        Assert.Equal((float)expected, FaderMath.Normalize(value, min, max), 5);
    }

    [Fact]
    public void Normalize_clamps_out_of_range()
    {
        Assert.Equal(0f, FaderMath.Normalize(-100.0, -12.0, 60.0), 5);
        Assert.Equal(1f, FaderMath.Normalize(100.0, -12.0, 60.0), 5);
    }

    [Theory]
    [InlineData(-18.0, 18.0)]
    [InlineData(-12.0, 60.0)]
    [InlineData(1.0, 3000.0)]   // FX delay time range
    public void Normalize_and_Denormalize_round_trip(double min, double max)
    {
        double mid = (min + max) / 2.0;
        float normalized = FaderMath.Normalize(mid, min, max);
        Assert.Equal(mid, FaderMath.Denormalize(normalized, min, max), 4);
    }
}
