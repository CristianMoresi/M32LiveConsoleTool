using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Domain;

/// <summary>
/// A mix bus of the M32 (one of the 16 available).
/// </summary>
/// <remarks>
/// A bus groups several channels and routes them as a sub-mix (for example "all
/// the drum mics" or "the lead singer's monitors"). Unlike a
/// <see cref="MixerChannel"/>, a bus has no preamp (the signal is already
/// processed by the channels), so the <see cref="Modules.AntiClipper"/> never
/// acts on it. Its base OSC address is <c>/bus/XX/...</c>.
/// </remarks>
public class Bus : AudioDevice
{
    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a mix bus with its basic parameters.
    /// </summary>
    /// <param name="name">Human-readable bus name (e.g. "Drums", "Monitors Lead").</param>
    /// <param name="oscNumber">Bus number on the console, 1-16.</param>
    /// <param name="targetLevelDb">Target level captured during the sound check.</param>
    public Bus(string name, int oscNumber, double targetLevelDb)
        : base(name, oscNumber, targetLevelDb)
    {
        // Nothing bus-specific to initialize: the sample history and the rest of
        // the state come from the AudioDevice base class.
    }

    // ─────────────────────────────────────────────────────────────────────
    // Overrides of AudioDevice
    // ─────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override void ApplyCorrection(double deltaDb)
    {
        // Same logic as MixerChannel: the fader is the last stage before the
        // post-fader meter, so adding deltaDb to the current level and converting
        // back to a normalized position gives the new value to send.
        double currentDb = FaderMath.FaderToDb(lastSentFader);
        double newDb = currentDb + deltaDb;
        lastSentFader = FaderMath.DbToFader(newDb);
    }

    /// <inheritdoc/>
    protected override string BuildOscAddress()
    {
        return $"/bus/{oscNumber:D2}/mix/fader";
    }

    /// <inheritdoc/>
    protected override string BuildMuteOscAddress()
    {
        return $"/bus/{oscNumber:D2}/mix/on";
    }
}
