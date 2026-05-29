using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Domain;

/// <summary>
/// A single meter sample of an audio device captured at one instant. Holds the
/// four values of a channel's processing chain (post-trim, gate gain reduction,
/// compressor gain reduction, post-fader) or, for a bus, only the post-fader.
/// </summary>
/// <remarks>
/// <para>
/// The M32 publishes a channel's meters as blocks of 4 floats (the
/// <c>/meters/6</c> bundle) and a bus's meter as one float inside the bus-page
/// bundle (<c>/meters/2</c>). This class unifies both cases: channel samples have
/// all fields valid; bus samples have only <see cref="PostFaderLinear"/> valid
/// and the other three set to <see cref="float.NaN"/> to mean "not applicable".
/// </para>
/// <para>
/// Samples are immutable: once created, their values never change, so they are
/// safe to share across threads without synchronization.
/// </para>
/// <para>
/// Conversion to decibels is done on demand through the <c>Db</c> properties,
/// which apply <see cref="FaderMath.MeterToDb"/>.
/// </para>
/// </remarks>
public class MeterSample
{
    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private readonly DateTime timestamp;
    private readonly float postTrimLinear;
    private readonly float gateGainReductionLinear;
    private readonly float compressorGainReductionLinear;
    private readonly float postFaderLinear;

    // ─────────────────────────────────────────────────────────────────────
    // Constructors
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a full sample with the four values of a channel's processing chain.
    /// Captures the current instant as the timestamp.
    /// </summary>
    /// <param name="postTrimLinear">Post-trim level, linear, range [0, 1.0+].</param>
    /// <param name="gateGainReductionLinear">Gate gain reduction, linear [0, 1.0].</param>
    /// <param name="compressorGainReductionLinear">Compressor gain reduction, linear [0, 1.0].</param>
    /// <param name="postFaderLinear">Post-fader level, linear, range [0, 1.0+].</param>
    public MeterSample(
        float postTrimLinear,
        float gateGainReductionLinear,
        float compressorGainReductionLinear,
        float postFaderLinear)
    {
        this.timestamp = DateTime.UtcNow;
        this.postTrimLinear = postTrimLinear;
        this.gateGainReductionLinear = gateGainReductionLinear;
        this.compressorGainReductionLinear = compressorGainReductionLinear;
        this.postFaderLinear = postFaderLinear;
    }

    /// <summary>
    /// Creates a sample with only the post-fader value, marking the other three
    /// fields as <see cref="float.NaN"/>. Intended for devices that report only
    /// post-fader (typically buses, from the <c>/meters/2</c> bundle).
    /// </summary>
    /// <param name="postFaderLinear">Post-fader level, linear, range [0, 1.0+].</param>
    public MeterSample(float postFaderLinear)
        : this(float.NaN, float.NaN, float.NaN, postFaderLinear)
    {
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties (read-only: the sample is immutable)
    // ─────────────────────────────────────────────────────────────────────

    public DateTime Timestamp
    {
        get { return timestamp; }
    }

    /// <summary>
    /// Post-trim level, linear. Returns <see cref="float.NaN"/> if the sample
    /// does not include this value (for example, if it comes from a bus).
    /// </summary>
    public float PostTrimLinear
    {
        get { return postTrimLinear; }
    }

    /// <summary>
    /// Gate gain reduction, linear. Returns <see cref="float.NaN"/> if not
    /// applicable.
    /// </summary>
    public float GateGainReductionLinear
    {
        get { return gateGainReductionLinear; }
    }

    /// <summary>
    /// Compressor gain reduction, linear. Returns <see cref="float.NaN"/> if not
    /// applicable.
    /// </summary>
    public float CompressorGainReductionLinear
    {
        get { return compressorGainReductionLinear; }
    }

    /// <summary>
    /// Post-fader level, linear. Always present.
    /// </summary>
    public float PostFaderLinear
    {
        get { return postFaderLinear; }
    }

    /// <summary>
    /// Post-trim level converted to decibels. Returns <see cref="double.NaN"/> if
    /// the sample does not include this value.
    /// </summary>
    public double PostTrimDb
    {
        get
        {
            if (float.IsNaN(postTrimLinear))
            {
                return double.NaN;
            }
            return FaderMath.MeterToDb(postTrimLinear);
        }
    }

    /// <summary>
    /// Post-fader level converted to decibels. Always returns a valid value (very
    /// low ones are clamped to -90 dB by <see cref="FaderMath.MeterToDb"/>).
    /// </summary>
    public double PostFaderDb
    {
        get { return FaderMath.MeterToDb(postFaderLinear); }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the post-fader level in decibels. Shortcut kept for the
    /// AutoLeveler, which only needs the post-fader value.
    /// </summary>
    public double AsDb()
    {
        return PostFaderDb;
    }

    /// <summary>
    /// Returns this sample's age in milliseconds relative to the current instant.
    /// </summary>
    /// <remarks>
    /// Since <see cref="Timestamp"/> is captured in UTC, the difference is also
    /// taken in UTC so the calculation is immune to time-zone or clock changes.
    /// </remarks>
    public double AgeMs()
    {
        return (DateTime.UtcNow - timestamp).TotalMilliseconds;
    }
}
