namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Every effect available in the M32/X32 FX rack, in the canonical order of the
/// FX slots 1-4 enum (the value equals the <c>/fx/N/type</c> index for slots 1-4).
/// Slots 5-8 expose a subset in a different order; <see cref="FxEffectCatalog"/>
/// maps both onto this single enum so any detected effect is identifiable.
/// </summary>
public enum FxEffectType
{
    HallReverb, Ambience, RichPlateReverb, RoomReverb, ChamberReverb,
    PlateReverb, VintageReverb, VintageRoom, GatedReverb, ReverseReverb,
    StereoDelay, ThreeTapDelay, FourTapDelay, StereoChorus, StereoFlanger,
    StereoPhaser, DimensionalChorus, MoodFilter, RotarySpeaker, TremoloPanner,
    Suboctaver, DelayChamber, ChorusChamber, FlangerChamber, DelayChorus,
    DelayFlanger, ModulationDelay, DualGraphicEq, GraphicEq, DualTubeEq,
    TubeEq, DualDeEsser, DeEsser, PultecEq, DualPultecEq,
    PultecProEq, DualPultecProEq, WaveDesigner, PrecisionLimiter, Combinator,
    DualCombinator, FairCompressor, FairCompressorMono, DualFairCompressor,
    LeisureCompressor, DualLeisureCompressor, UltimoCompressor, DualUltimoCompressor,
    DualEnhancer, Enhancer, DualExciter, Exciter, StereoImager, EdisonEx1,
    SoundMaxer, DualGuitarAmp, GuitarAmp, DualOverdrive, Overdrive,
    DualPitchShifter, PitchShifter
}

/// <summary>
/// Result of resolving an <c>/fx/N/type</c> value: the identified effect, its
/// readable name and, when it carries a delay time, the matching
/// <see cref="DelayEffectType"/>.
/// </summary>
public sealed class DetectedEffect
{
    public DetectedEffect(FxEffectType effectType, string name, DelayEffectType? delayType)
    {
        EffectType = effectType;
        Name = name;
        DelayType = delayType;
    }

    /// <summary>The identified effect type (any effect, not only delays).</summary>
    public FxEffectType EffectType { get; }

    /// <summary>Readable effect name, e.g. "Stereo Delay" or "Hall Reverb".</summary>
    public string Name { get; }

    /// <summary>The delay type if this effect carries a delay time; otherwise <c>null</c>.</summary>
    public DelayEffectType? DelayType { get; }

    /// <summary><c>true</c> if this effect is a delay (has a tempo-syncable time).</summary>
    public bool IsDelay => DelayType.HasValue;
}

/// <summary>
/// Maps the M32/X32 effect-type enum reported by <c>/fx/N/type</c> to a
/// <see cref="FxEffectType"/>, a readable name and, for delays, a
/// <see cref="DelayEffectType"/>.
/// </summary>
/// <remarks>
/// FX slots 1-4 can host any effect (enum 0-60, including all delays); slots 5-8
/// are insert-only (enum 0-33, a subset in a different order) and never hold a
/// delay. Both are resolved into the single <see cref="FxEffectType"/> enum.
/// </remarks>
public static class FxEffectCatalog
{
    // Readable name per FxEffectType, indexed by (int)FxEffectType.
    private static readonly string[] Names =
    {
        "Hall Reverb", "Ambience", "Rich Plate Reverb", "Room Reverb", "Chamber Reverb",
        "Plate Reverb", "Vintage Reverb", "Vintage Room", "Gated Reverb", "Reverse Reverb",
        "Stereo Delay", "3-Tap Delay", "4-Tap Delay", "Stereo Chorus", "Stereo Flanger",
        "Stereo Phaser", "Dimensional Chorus", "Mood Filter", "Rotary Speaker", "Tremolo/Panner",
        "Suboctaver", "Delay/Chamber", "Chorus/Chamber", "Flanger/Chamber", "Delay/Chorus",
        "Delay/Flanger", "Modulation Delay", "Dual Graphic EQ", "Graphic EQ", "Dual Tube EQ",
        "Tube EQ", "Dual DeEsser", "DeEsser", "Pultec EQ", "Dual Pultec EQ",
        "Pultec Pro EQ", "Dual Pultec Pro EQ", "Wave Designer", "Precision Limiter", "Combinator",
        "Dual Combinator", "Fair Compressor", "Fair Compressor (mono)", "Dual Fair Compressor",
        "Leisure Compressor", "Dual Leisure Compressor", "Ultimo Compressor", "Dual Ultimo Compressor",
        "Dual Enhancer", "Enhancer", "Dual Exciter", "Exciter", "Stereo Imager", "Edison EX1",
        "Sound Maxer", "Dual Guitar Amp", "Guitar Amp", "Dual Overdrive", "Overdrive",
        "Dual Pitch Shifter", "Pitch Shifter"
    };

    // FxEffectType for each FX slots 5-8 enum index (insert effects, different order).
    private static readonly FxEffectType[] Slots5To8 =
    {
        FxEffectType.DualGraphicEq, FxEffectType.GraphicEq, FxEffectType.DualTubeEq, FxEffectType.TubeEq,
        FxEffectType.DualDeEsser, FxEffectType.DeEsser, FxEffectType.PultecEq, FxEffectType.DualPultecEq,
        FxEffectType.PultecProEq, FxEffectType.DualPultecProEq, FxEffectType.WaveDesigner,
        FxEffectType.PrecisionLimiter, FxEffectType.FairCompressor, FxEffectType.FairCompressorMono,
        FxEffectType.DualFairCompressor, FxEffectType.LeisureCompressor, FxEffectType.DualLeisureCompressor,
        FxEffectType.UltimoCompressor, FxEffectType.DualUltimoCompressor, FxEffectType.DualEnhancer,
        FxEffectType.Enhancer, FxEffectType.DualExciter, FxEffectType.Exciter, FxEffectType.StereoImager,
        FxEffectType.EdisonEx1, FxEffectType.SoundMaxer, FxEffectType.DualGuitarAmp, FxEffectType.GuitarAmp,
        FxEffectType.DualOverdrive, FxEffectType.Overdrive, FxEffectType.StereoPhaser, FxEffectType.MoodFilter,
        FxEffectType.TremoloPanner, FxEffectType.Suboctaver
    };

    /// <summary>Number of distinct effect types known to this catalog.</summary>
    public static int EffectCount => Names.Length;

    /// <summary>Readable name for an effect type.</summary>
    public static string GetName(FxEffectType type)
    {
        int index = (int)type;
        return index >= 0 && index < Names.Length ? Names[index] : type.ToString();
    }

    /// <summary>
    /// Resolves a <c>/fx/N/type</c> value into the identified effect. Returns
    /// <c>null</c> if the slot or type index is out of range.
    /// </summary>
    /// <param name="slot">FX slot number, 1-8.</param>
    /// <param name="typeIndex">Effect-type enum value reported by the console.</param>
    public static DetectedEffect? Resolve(int slot, int typeIndex)
    {
        FxEffectType? type = ResolveType(slot, typeIndex);
        if (type == null)
        {
            return null;
        }

        return new DetectedEffect(type.Value, GetName(type.Value), DelayTypeOf(type.Value));
    }

    /// <summary>Maps a (slot, /fx/N/type index) pair to a <see cref="FxEffectType"/>, or <c>null</c> if out of range.</summary>
    private static FxEffectType? ResolveType(int slot, int typeIndex)
    {
        if (slot < 1 || slot > 8)
        {
            return null;
        }

        if (slot <= 4)
        {
            // Slots 1-4 enum is the canonical FxEffectType order.
            return typeIndex >= 0 && typeIndex < Names.Length ? (FxEffectType)typeIndex : null;
        }

        // Slots 5-8 use the insert-only subset/order.
        return typeIndex >= 0 && typeIndex < Slots5To8.Length ? Slots5To8[typeIndex] : null;
    }

    /// <summary>Maps a <see cref="FxEffectType"/> to a <see cref="DelayEffectType"/>, or <c>null</c> if not a delay.</summary>
    private static DelayEffectType? DelayTypeOf(FxEffectType type)
    {
        return type switch
        {
            FxEffectType.StereoDelay => DelayEffectType.StereoDelay,
            FxEffectType.ThreeTapDelay => DelayEffectType.ThreeTapDelay,
            FxEffectType.FourTapDelay => DelayEffectType.FourTapDelay,
            FxEffectType.DelayChamber => DelayEffectType.DelayChamber,
            FxEffectType.DelayChorus => DelayEffectType.DelayChorus,
            FxEffectType.DelayFlanger => DelayEffectType.DelayFlanger,
            FxEffectType.ModulationDelay => DelayEffectType.ModulationDelay,
            _ => null
        };
    }
}
