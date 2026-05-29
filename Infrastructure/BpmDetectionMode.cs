namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Operating mode of the <see cref="Modules.BpmDetector"/>. Determines how
/// incoming transients are interpreted to infer the tempo of the music.
/// </summary>
/// <remarks>
/// <para>
/// The engineer chooses the mode based on the source feeding the rhythmic
/// reference channel. Each mode has different reliability:
/// </para>
/// <list type="bullet">
///   <item><see cref="Click"/>: highest reliability. Every transient is exactly
///   one quarter note. Only suitable for a backing-track click source.</item>
///   <item><see cref="BeatTracking"/>: medium reliability. Assumes transients
///   mark some regular subdivision of the pulse and infers the most likely BPM.
///   Works on steady patterns (snare backbeat, kick on quarters); struggles with
///   breakdowns, fills and tempo changes.</item>
///   <item><see cref="ManualTap"/>: high reliability but needs active input from
///   the engineer. Each key press counts as one quarter note.</item>
/// </list>
/// </remarks>
public enum BpmDetectionMode
{
    /// <summary>
    /// The watched channel emits the pulse directly (each transient = 1 quarter
    /// note). Recommended when a backing-track click is available.
    /// </summary>
    Click,

    /// <summary>
    /// The watched channel carries a rhythmic pattern (snare, kick, percussion).
    /// The detector infers tempo from the regularity of the inter-onset
    /// intervals. Works reasonably well on steady patterns.
    /// </summary>
    BeatTracking,

    /// <summary>
    /// Tempo is set exclusively by manual taps from the engineer (tap tempo).
    /// The watched channel is ignored.
    /// </summary>
    ManualTap
}
