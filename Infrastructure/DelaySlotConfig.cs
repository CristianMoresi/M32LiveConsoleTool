using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Modules;

/// <summary>
/// Configuration of one M32 effects delay slot. Holds the delay effect type
/// loaded in the slot on the console (so the time is written to the right
/// parameter) and whether the slot follows the detected BPM or a fixed manual
/// time.
/// </summary>
/// <remarks>
/// <para>
/// Each M32 effects slot can be used independently. A single show might have:
/// </para>
/// <list type="bullet">
///   <item>Slot 1: Stereo Delay, automatic, dotted eighth (synced lead-guitar delay).</item>
///   <item>Slot 2: 3-Tap Delay, automatic, eighth (lead-vocal delay).</item>
///   <item>Slot 3: Stereo Delay, manual, 80 ms (fixed slapback for backing vocals).</item>
/// </list>
/// <para>
/// The class is immutable: once created, its values do not change. To change a
/// slot's configuration, replace the instance in the <see cref="DelayController"/>.
/// </para>
/// <para>
/// Built through the static factory methods <see cref="Automatic"/> and
/// <see cref="Manual"/>, both of which take the slot's effect type.
/// </para>
/// </remarks>
public class DelaySlotConfig
{
    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private readonly DelayEffectType effectType;
    private readonly bool isAutomatic;
    private readonly BeatType beat;
    private readonly double manualMs;

    // ─────────────────────────────────────────────────────────────────────
    // Private constructor
    // ─────────────────────────────────────────────────────────────────────

    private DelaySlotConfig(DelayEffectType effectType, bool isAutomatic, BeatType beat, double manualMs)
    {
        if (!Enum.IsDefined(effectType) || !Enum.IsDefined(beat)) throw new ArgumentOutOfRangeException(nameof(effectType));
        this.effectType = effectType;
        this.isAutomatic = isAutomatic;
        this.beat = beat;
        this.manualMs = manualMs;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Static factory methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an automatic slot: the delay time is recomputed on every BPM change
    /// using the given rhythmic figure and written to the effect's time parameter.
    /// </summary>
    /// <param name="effectType">The delay effect loaded in the slot on the console.</param>
    /// <param name="beat">Rhythmic figure the slot syncs to.</param>
    public static DelaySlotConfig Automatic(DelayEffectType effectType, BeatType beat)
    {
        return new DelaySlotConfig(effectType, true, beat, 0.0);
    }

    /// <summary>
    /// Creates a manual slot: the delay time stays fixed at the given value and is
    /// not affected by BPM changes.
    /// </summary>
    /// <param name="effectType">The delay effect loaded in the slot on the console.</param>
    /// <param name="manualMs">Delay time in milliseconds. Must be positive.</param>
    public static DelaySlotConfig Manual(DelayEffectType effectType, double manualMs)
    {
        if (!double.IsFinite(manualMs) || manualMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(manualMs),
                "Manual delay time must be positive.");
        }
        return new DelaySlotConfig(effectType, false, BeatType.Quarter, manualMs);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The delay effect loaded in this slot on the console. Determines which FX
    /// parameter carries the time (see <see cref="DelayEffects.TimeParameterNumber"/>).
    /// </summary>
    public DelayEffectType EffectType
    {
        get { return effectType; }
    }

    /// <summary>
    /// <c>true</c> if the slot updates automatically on every BPM change;
    /// <c>false</c> if it is manual.
    /// </summary>
    public bool IsAutomatic
    {
        get { return isAutomatic; }
    }

    /// <summary>
    /// Rhythmic figure of the slot when <see cref="IsAutomatic"/> is <c>true</c>.
    /// Meaningless in manual mode.
    /// </summary>
    public BeatType Beat
    {
        get { return beat; }
    }

    /// <summary>
    /// Fixed delay time in milliseconds when <see cref="IsAutomatic"/> is
    /// <c>false</c>. Meaningless in automatic mode.
    /// </summary>
    public double ManualMs
    {
        get { return manualMs; }
    }

    /// <summary>
    /// Computes the effective delay time in milliseconds for a given BPM. In
    /// automatic mode it applies the rhythmic figure; in manual mode it returns
    /// the fixed value, ignoring the BPM.
    /// </summary>
    /// <param name="bpm">Current BPM. Only used in automatic mode.</param>
    public double GetDelayMs(double bpm)
    {
        if (!isAutomatic)
        {
            return manualMs;
        }

        if (!double.IsFinite(bpm) || bpm <= 0)
        {
            return 0.0;
        }

        return FaderMath.DelayMsForBeat(bpm, beat);
    }

    /// <summary>
    /// Text representation useful for logs and debugging.
    /// </summary>
    public override string ToString()
    {
        string mode = isAutomatic ? $"Auto({beat})" : $"Manual({manualMs:F0}ms)";
        return $"{effectType}/{mode}";
    }
}
