namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Standard rhythmic figure for syncing delay times to the musical tempo.
/// Covers five base figures (whole, half, quarter, eighth and slapback) in
/// their three variants (straight, dotted, triplet) — fifteen selectable
/// figures in total.
/// </summary>
/// <remarks>
/// <para>
/// Each figure's duration is computed relative to the quarter note:
/// </para>
/// <list type="bullet">
///   <item><strong>Straight</strong>: the figure's base duration. Whole = 4
///   quarters, half = 2 quarters, quarter = 1 quarter, eighth = 1/2 quarter,
///   slapback = 1/4 quarter (a sixteenth note, named this way by professional
///   convention because at typical BPMs its time lands in the classic slapback
///   range).</item>
///   <item><strong>Dotted</strong>: 1.5x the straight figure.</item>
///   <item><strong>Triplet</strong>: 2/3 of the straight figure (three notes in
///   the space of two).</item>
/// </list>
/// <para>
/// Conversion to milliseconds for a given BPM is done by
/// <see cref="FaderMath.DelayMsForBeat"/>.
/// </para>
/// <para>
/// <strong>On synced slapback</strong>: the classic slapback is a short delay
/// (typically 100-250 ms) that adds presence and depth to vocals and guitars
/// without being heard as a distinct repeat. It was traditionally set to a fixed
/// time in milliseconds; this enum lets it follow the tempo instead. At 60 BPM
/// it lasts 250 ms, at 120 BPM 125 ms, at 150 BPM 100 ms. Outside that BPM range
/// slapback can end up too short or too long and another figure is preferable.
/// </para>
/// </remarks>
public enum BeatType
{
    /// <summary>Whole note (4 quarters).</summary>
    Whole,

    /// <summary>Dotted whole note (6 quarters).</summary>
    DottedWhole,

    /// <summary>Whole-note triplet (8/3 quarters).</summary>
    WholeTriplet,

    /// <summary>Half note (2 quarters).</summary>
    Half,

    /// <summary>Dotted half note (3 quarters).</summary>
    DottedHalf,

    /// <summary>Half-note triplet (4/3 quarters).</summary>
    HalfTriplet,

    /// <summary>Quarter note (1 quarter). The reference figure.</summary>
    Quarter,

    /// <summary>Dotted quarter note (1.5 quarters).</summary>
    DottedQuarter,

    /// <summary>Quarter-note triplet (2/3 quarter).</summary>
    QuarterTriplet,

    /// <summary>Eighth note (1/2 quarter).</summary>
    Eighth,

    /// <summary>Dotted eighth note (3/4 quarter).</summary>
    DottedEighth,

    /// <summary>Eighth-note triplet (1/3 quarter).</summary>
    EighthTriplet,

    /// <summary>
    /// Slapback (1/4 quarter, a sixteenth note). Produces short delay times in
    /// the classic slapback range.
    /// </summary>
    Slapback,

    /// <summary>Dotted slapback (3/8 quarter).</summary>
    DottedSlapback,

    /// <summary>Slapback triplet (1/6 quarter).</summary>
    SlapbackTriplet
}
