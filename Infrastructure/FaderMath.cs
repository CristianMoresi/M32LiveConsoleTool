namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Math helpers for converting between the domain's units: fader position
/// (linear 0-1), level in decibels, linear meter values, and delay times in
/// milliseconds derived from a tempo and a rhythmic figure.
/// </summary>
/// <remarks>
/// <para>
/// All functions are static and pure: they hold no state and always return the
/// same result for the same arguments, so they are safe to call from any thread.
/// </para>
/// <para>
/// The fader↔dB conversions use the X32/M32 four-segment piecewise-linear taper
/// from the official protocol notes: fader 0.0 → -inf/-90 dB, 0.0625 → -60 dB,
/// 0.25 → -30 dB, 0.5 → -10 dB, 0.75 → 0 dB (unity) and 1.0 → +10 dB.
/// </para>
/// </remarks>
public static class FaderMath
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Lowest dB value treated as "absolute silence" for meters.</summary>
    private const double METER_FLOOR_DB = -90.0;

    /// <summary>Lowest linear meter value accepted before returning the floor.</summary>
    private const double METER_FLOOR_LINEAR = 0.0000316;  // 10^(-90/20)

    // ─────────────────────────────────────────────────────────────────────
    // Meter ↔ dB conversions
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a linear meter value (range [0, 8.0] on the M32 with internal
    /// headroom) to its equivalent in decibels.
    /// </summary>
    /// <param name="linear">Linear meter value.</param>
    /// <returns>Value in dB, clamped to <see cref="METER_FLOOR_DB"/> for very low inputs.</returns>
    public static double MeterToDb(float linear)
    {
        if (!float.IsFinite(linear) || linear <= METER_FLOOR_LINEAR)
        {
            return METER_FLOOR_DB;
        }
        return 20.0 * Math.Log10(linear);
    }

    /// <summary>
    /// Converts a value in decibels to its linear equivalent.
    /// </summary>
    public static double DbToLinear(double db)
    {
        return Math.Pow(10.0, db / 20.0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fader ↔ dB conversions (M32 taper approximation)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a linear fader position (0.0 to 1.0) to the level in dB it
    /// represents, using the X32/M32 four-segment taper.
    /// </summary>
    public static double FaderToDb(float fader)
    {
        if (fader <= 0f)
        {
            return double.NegativeInfinity;
        }
        if (fader >= 1f)
        {
            return 10.0;
        }
        if (fader >= 0.5f)
        {
            return fader * 40.0 - 30.0;   // 0.5 -> -10 dB, 0.75 -> 0 dB, 1.0 -> +10 dB
        }
        if (fader >= 0.25f)
        {
            return fader * 80.0 - 50.0;   // 0.25 -> -30 dB, 0.5 -> -10 dB
        }
        if (fader >= 0.0625f)
        {
            return fader * 160.0 - 70.0;  // 0.0625 -> -60 dB, 0.25 -> -30 dB
        }
        return fader * 480.0 - 90.0;       // 0.0 -> -90 dB, 0.0625 -> -60 dB
    }

    /// <summary>
    /// Converts a level in decibels to the linear fader position (0.0 to 1.0)
    /// the M32 expects, inverting the four-segment taper.
    /// </summary>
    public static float DbToFader(double db)
    {
        if (double.IsNegativeInfinity(db) || db <= -90.0)
        {
            return 0f;
        }
        if (db >= 10.0)
        {
            return 1f;
        }
        if (db < -60.0)
        {
            return (float)((db + 90.0) / 480.0);
        }
        if (db < -30.0)
        {
            return (float)((db + 70.0) / 160.0);
        }
        if (db < -10.0)
        {
            return (float)((db + 50.0) / 80.0);
        }
        return (float)((db + 30.0) / 40.0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // BPM + figure → milliseconds
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a rhythmic figure to its duration in milliseconds for a given BPM.
    /// </summary>
    /// <param name="bpm">Tempo in beats per minute. Must be positive.</param>
    /// <param name="beat">Rhythmic figure.</param>
    /// <returns>Duration of the figure in milliseconds.</returns>
    /// <remarks>
    /// <para>
    /// Every figure is a multiplicative factor over the quarter-note duration.
    /// At 120 BPM a quarter note lasts 500 ms; the other figures are obtained by
    /// multiplying that value by the corresponding factor.
    /// </para>
    /// <para>
    /// Returns 0 if <paramref name="bpm"/> is 0 or negative.
    /// </para>
    /// </remarks>
    public static double DelayMsForBeat(double bpm, BeatType beat)
    {
        if (!double.IsFinite(bpm) || bpm <= 0)
        {
            return 0.0;
        }

        double quarterMs = 60000.0 / bpm;

        switch (beat)
        {
            // Whole note and variants (4 quarters base).
            case BeatType.Whole: return quarterMs * 4.0;
            case BeatType.DottedWhole: return quarterMs * 4.0 * 1.5;
            case BeatType.WholeTriplet: return quarterMs * 4.0 * 2.0 / 3.0;

            // Half note and variants (2 quarters base).
            case BeatType.Half: return quarterMs * 2.0;
            case BeatType.DottedHalf: return quarterMs * 2.0 * 1.5;
            case BeatType.HalfTriplet: return quarterMs * 2.0 * 2.0 / 3.0;

            // Quarter note and variants (1 quarter base).
            case BeatType.Quarter: return quarterMs;
            case BeatType.DottedQuarter: return quarterMs * 1.5;
            case BeatType.QuarterTriplet: return quarterMs * 2.0 / 3.0;

            // Eighth note and variants (1/2 quarter base).
            case BeatType.Eighth: return quarterMs * 0.5;
            case BeatType.DottedEighth: return quarterMs * 0.5 * 1.5;
            case BeatType.EighthTriplet: return quarterMs * 0.5 * 2.0 / 3.0;

            // Slapback and variants (1/4 quarter base, a sixteenth note).
            case BeatType.Slapback: return quarterMs * 0.25;
            case BeatType.DottedSlapback: return quarterMs * 0.25 * 1.5;
            case BeatType.SlapbackTriplet: return quarterMs * 0.25 * 2.0 / 3.0;

            default: return quarterMs;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Normalized [0,1] <-> physical range (for "linf" OSC parameters)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a physical value in [<paramref name="min"/>, <paramref name="max"/>]
    /// to the normalized [0, 1] range the console expects for "linf" parameters
    /// (head amp gain, FX delay time, etc.). Clamps to [0, 1].
    /// </summary>
    public static float Normalize(double value, double min, double max)
    {
        if (!double.IsFinite(value) || !double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            return 0f;
        }
        double normalized = (value - min) / (max - min);
        if (normalized < 0.0) normalized = 0.0;
        if (normalized > 1.0) normalized = 1.0;
        return (float)normalized;
    }

    /// <summary>
    /// Inverse of <see cref="Normalize"/>: maps a normalized [0, 1] value back to
    /// the physical range [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    public static double Denormalize(float normalized, double min, double max)
    {
        return min + normalized * (max - min);
    }
}
