using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Domain;

/// <summary>
/// An input channel of the M32 (one of the 32 available).
/// </summary>
/// <remarks>
/// Unlike a <see cref="Bus"/>, a channel has a preamp (which the
/// <see cref="Modules.AntiClipper"/> can attenuate as a safety measure). A
/// channel's base OSC address is <c>/ch/XX/...</c>, where XX is the channel
/// number as two zero-padded digits (required by the M32 firmware).
/// </remarks>
public class MixerChannel : AudioDevice
{
    // ─────────────────────────────────────────────────────────────────────
    // Fields specific to a channel (not in AudioDevice)
    // ─────────────────────────────────────────────────────────────────────

    private double preampGain;

    /// <summary>
    /// Index of the console head amp feeding this channel (0-127: local XLR
    /// 0-31, AES50-A 32-79, AES50-B 80-127), or -1 if the source is digital and
    /// has no analog head amp. Reported by the console via <c>/-ha/NN/index</c>
    /// and used by the AntiClipper to target the right preamp. Volatile because
    /// the OSC receive thread updates it.
    /// </summary>
    private volatile int headAmpIndex = -1;

    /// <summary>
    /// <c>true</c> once the console has answered this channel's <c>/-ha/NN/index</c>
    /// query. Distinguishes "not answered yet" (the query or its UDP reply was
    /// lost, so the MeterReader keeps re-asking) from "answered -1" (a genuine
    /// digital source with no analog head amp, which needs no retry).
    /// </summary>
    private volatile bool headAmpResolved;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an input channel with its basic parameters.
    /// </summary>
    /// <param name="name">Human-readable channel name (e.g. "Lead Vocal", "Kick").</param>
    /// <param name="oscNumber">Channel number on the console, 1-32.</param>
    /// <param name="targetLevelDb">Target level captured during the sound check.</param>
    /// <param name="preampGain">Initial preamp gain in dB.</param>
    public MixerChannel(string name, int oscNumber, double targetLevelDb, double preampGain)
        : base(name, oscNumber, targetLevelDb)
    {
        this.preampGain = preampGain;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Channel-specific properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Current preamp gain in decibels.
    /// </summary>
    public double PreampGain
    {
        get { return preampGain; }
        set { preampGain = value; }
    }

    /// <summary>
    /// Console head amp index feeding this channel, or -1 if the source is
    /// digital (no analog head amp).
    /// </summary>
    public int HeadAmpIndex
    {
        get { return headAmpIndex; }
        set { headAmpIndex = value; }
    }

    /// <summary>
    /// <c>true</c> once the console has answered the head amp query for this
    /// channel (even with -1, meaning a digital source). While <c>false</c>, the
    /// MeterReader periodically re-sends the query in case the original datagram
    /// was lost over UDP.
    /// </summary>
    public bool HeadAmpResolved
    {
        get { return headAmpResolved; }
        set { headAmpResolved = value; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Overrides of AudioDevice
    // ─────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override void ApplyCorrection(double deltaDb)
    {
        // The fader is the last stage before the post-fader meter, so an N dB
        // change at the fader is an N dB change in the monitored level. Start from
        // the last position we sent to the console, apply the delta in the dB
        // domain (where the AutoLeveler expresses the correction) and convert back
        // to a normalized fader position.
        double currentDb = FaderMath.FaderToDb(lastSentFader);
        double newDb = currentDb + deltaDb;
        lastSentFader = FaderMath.DbToFader(newDb);
    }

    /// <inheritdoc/>
    protected override string BuildOscAddress()
    {
        return $"/ch/{oscNumber:D2}/mix/fader";
    }

    /// <inheritdoc/>
    protected override string BuildMuteOscAddress()
    {
        return $"/ch/{oscNumber:D2}/mix/on";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Channel-specific methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reduces the preamp gain by a fixed amount (irreversible within the session).
    /// </summary>
    /// <param name="dB">Amount to reduce, in decibels. Must be positive.</param>
    /// <remarks>
    /// This only updates the channel's internal state. Sending the OSC message — to
    /// the channel's head amp (<c>/headamp/NNN/gain</c>) or, for a digital source
    /// with no head amp, to its digital trim (<c>/ch/NN/preamp/trim</c>) — is the
    /// <see cref="Modules.AntiClipper"/>'s responsibility, keeping the domain
    /// independent of the network.
    /// </remarks>
    public void ReducePreampGain(double dB)
    {
        if (dB <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dB),
                "Preamp reduction must be positive (the AntiClipper only attenuates, never boosts).");
        }

        preampGain -= dB;
    }
}
