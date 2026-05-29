using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for resolving M32 /fx/N/type values into effect names and delay types.
/// </summary>
public class FxEffectCatalogTests
{
    [Fact]
    public void Resolves_delays_in_slots_1_to_4()
    {
        Assert.Equal(DelayEffectType.StereoDelay, FxEffectCatalog.Resolve(1, 10)!.DelayType);
        Assert.Equal(DelayEffectType.ThreeTapDelay, FxEffectCatalog.Resolve(1, 11)!.DelayType);
        Assert.Equal(DelayEffectType.FourTapDelay, FxEffectCatalog.Resolve(2, 12)!.DelayType);
        Assert.Equal(DelayEffectType.DelayChamber, FxEffectCatalog.Resolve(3, 21)!.DelayType);
        Assert.Equal(DelayEffectType.DelayChorus, FxEffectCatalog.Resolve(4, 24)!.DelayType);
        Assert.Equal(DelayEffectType.DelayFlanger, FxEffectCatalog.Resolve(1, 25)!.DelayType);
        Assert.Equal(DelayEffectType.ModulationDelay, FxEffectCatalog.Resolve(1, 26)!.DelayType);
    }

    [Fact]
    public void Non_delay_effect_has_no_delay_type()
    {
        DetectedEffect? hall = FxEffectCatalog.Resolve(1, 0);

        Assert.NotNull(hall);
        Assert.Equal("Hall Reverb", hall!.Name);
        Assert.False(hall.IsDelay);
    }

    [Fact]
    public void Slots_5_to_8_never_resolve_to_a_delay()
    {
        // The insert-only enum has 34 entries (0-33), none of which is a delay.
        for (int typeIndex = 0; typeIndex < 34; typeIndex++)
        {
            DetectedEffect? fx = FxEffectCatalog.Resolve(5, typeIndex);
            Assert.NotNull(fx);
            Assert.False(fx!.IsDelay);
        }
    }

    [Fact]
    public void Out_of_range_returns_null()
    {
        Assert.Null(FxEffectCatalog.Resolve(0, 0));
        Assert.Null(FxEffectCatalog.Resolve(9, 0));
        Assert.Null(FxEffectCatalog.Resolve(1, 999));
        Assert.Null(FxEffectCatalog.Resolve(1, -1));
    }

    [Fact]
    public void Resolves_effect_type_in_slots_1_to_4()
    {
        Assert.Equal(FxEffectType.HallReverb, FxEffectCatalog.Resolve(1, 0)!.EffectType);
        Assert.Equal(FxEffectType.StereoDelay, FxEffectCatalog.Resolve(1, 10)!.EffectType);
        Assert.Equal(FxEffectType.PitchShifter, FxEffectCatalog.Resolve(1, 60)!.EffectType); // last index
    }

    [Fact]
    public void Resolves_insert_effect_type_in_slots_5_to_8()
    {
        Assert.Equal(FxEffectType.DualGraphicEq, FxEffectCatalog.Resolve(5, 0)!.EffectType);
        Assert.Equal(FxEffectType.StereoPhaser, FxEffectCatalog.Resolve(5, 30)!.EffectType);
        Assert.Equal(FxEffectType.Suboctaver, FxEffectCatalog.Resolve(8, 33)!.EffectType); // last insert index
    }

    [Fact]
    public void Catalog_registers_all_61_effect_types()
    {
        Assert.Equal(61, FxEffectCatalog.EffectCount);
    }
}
