namespace M32LiveConsoleTool.Domain;

/// <summary>
/// Abstract base for an M32 audio device: an input channel
/// (<see cref="MixerChannel"/>) or a mix bus (<see cref="Bus"/>).
/// </summary>
/// <remarks>
/// <para>
/// Holds the state common to every device (name, OSC number, target level, last
/// sent fader, current fader, mute state and a meter-sample history) and two
/// abstract methods each subclass implements: <see cref="ApplyCorrection"/> to
/// compute a level correction, and <see cref="GetOscAddress"/> to build the
/// device's fader address.
/// </para>
/// <para>
/// The sample history lives here (not only on channels) so the AutoLeveler can
/// compute moving averages over buses too. Access is guarded by an internal lock
/// because the OSC receive thread appends samples while several modules read
/// them: use <see cref="GetLatestSample"/> for the most recent sample and
/// <see cref="SnapshotHistory"/> to iterate the whole history safely.
/// </para>
/// </remarks>
public abstract class AudioDevice
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tolerance for treating two fader values as equal. The M32 works with
    /// 32-bit floats, so small rounding errors through the encode-send-receive-
    /// decode chain are unavoidable; a difference smaller than this is numeric
    /// noise, not real movement.
    /// </summary>
    public const float FADER_EPSILON = 0.001f;

    /// <summary>
    /// Maximum time (ms) a sample stays in the history. Set with margin over the
    /// AutoLeveler's typical window (2-3 s) so
    /// <see cref="Modules.MeterReader.GetMovingAverage"/> always has enough
    /// material when filtering by the exact window configured at runtime.
    /// </summary>
    private const int HISTORY_RETENTION_MS = 5000;

    // ─────────────────────────────────────────────────────────────────────
    // Protected fields
    // ─────────────────────────────────────────────────────────────────────

    protected string name;
    protected int oscNumber;
    protected double targetLevelDb;
    protected volatile float lastSentFader;
    protected volatile float currentFader;
    protected volatile bool faderInitialized;
    protected volatile bool isMuted;
    protected readonly List<MeterSample> sampleHistory;

    /// <summary>
    /// Lock guarding access to the sample history between the thread that appends
    /// samples (OSC receive) and the threads that read them (AutoLeveler,
    /// AntiClipper, etc.).
    /// </summary>
    private readonly object historyLock = new object();

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the state common to every audio device.
    /// </summary>
    /// <param name="name">Human-readable device name (e.g. "Lead Vocal" or "Drums Bus").</param>
    /// <param name="oscNumber">Device number on the console: 1-32 for channels, 1-16 for buses.</param>
    /// <param name="targetLevelDb">Target level in decibels captured during the sound check.</param>
    protected AudioDevice(string name, int oscNumber, double targetLevelDb)
    {
        this.name = name;
        this.oscNumber = oscNumber;
        this.targetLevelDb = targetLevelDb;
        this.lastSentFader = 0f;
        this.currentFader = 0f;
        this.faderInitialized = false;
        this.isMuted = false;
        this.sampleHistory = new List<MeterSample>();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Human-readable device name. Can be changed at runtime via the menu's
    /// rename flow. The name is part of the device's identity to the engineer but
    /// not to the console (which identifies devices by OSC number), so changing it
    /// does not affect ongoing OSC communication.
    /// </summary>
    public string Name
    {
        get { return name; }
        set { name = value; }
    }

    public int OscNumber
    {
        get { return oscNumber; }
    }

    public double TargetLevelDb
    {
        get { return targetLevelDb; }
        set { targetLevelDb = value; }
    }

    public float LastSentFader
    {
        get { return lastSentFader; }
        set { lastSentFader = value; }
    }

    /// <summary>
    /// Last fader position reported by the console, updated by the OSC receiver
    /// whenever a <c>/ch/XX/mix/fader</c> or <c>/bus/XX/mix/fader</c> message
    /// arrives for this device (see <see cref="UpdateFaderFromConsole"/>). The
    /// setter is also used by the leveler to optimistically record its own writes
    /// before the console echoes them back.
    /// </summary>
    public float CurrentFader
    {
        get { return currentFader; }
        set { currentFader = value; }
    }

    /// <summary>
    /// <c>true</c> once the console has reported this device's real fader position
    /// at least once. Until then the leveler must not act on the device: it does
    /// not yet know where the fader physically sits, and acting from the
    /// placeholder 0.0 (which maps to -inf dB) would be wrong.
    /// </summary>
    public bool FaderInitialized
    {
        get { return faderInitialized; }
    }

    public bool IsMuted
    {
        get { return isMuted; }
        set { isMuted = value; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Abstract methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a level correction in decibels, computing the new fader position
    /// and recording it in <see cref="LastSentFader"/>.
    /// </summary>
    /// <param name="deltaDb">
    /// Change relative to the current level (positive = up, negative = down). The
    /// value arrives already limited by the <see cref="Modules.AutoLeveler"/>'s
    /// slew rate; this method applies no extra smoothing.
    /// </param>
    /// <remarks>
    /// The domain layer does NOT send OSC messages: it only updates device state.
    /// The actual UDP send is done by the module that requested the correction
    /// (<see cref="Modules.AutoLeveler"/>), which owns the OSC client and gets the
    /// destination address via <see cref="GetOscAddress"/>.
    /// </remarks>
    public abstract void ApplyCorrection(double deltaDb);

    /// <summary>
    /// Returns the device's fader OSC address according to its type.
    /// </summary>
    /// <returns>OSC path, e.g. <c>/ch/01/mix/fader</c> or <c>/bus/03/mix/fader</c>.</returns>
    public abstract string GetOscAddress();

    /// <summary>
    /// Returns the device's mute (on/off) OSC address according to its type.
    /// </summary>
    /// <returns>OSC path, e.g. <c>/ch/01/mix/on</c> or <c>/bus/03/mix/on</c>.</returns>
    public abstract string GetMuteOscAddress();

    // ─────────────────────────────────────────────────────────────────────
    // Methods common to all subclasses
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends a sample to the history and drops any that fall outside the
    /// retention window. Guarded by the internal lock: safe to call from the OSC
    /// receive thread while other threads read the history.
    /// </summary>
    /// <param name="sample">Sample to add.</param>
    public void AddSample(MeterSample sample)
    {
        lock (historyLock)
        {
            sampleHistory.Add(sample);

            while (sampleHistory.Count > 0 &&
                   sampleHistory[0].AgeMs() > HISTORY_RETENTION_MS)
            {
                sampleHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Returns the most recent sample, or <c>null</c> if the history is empty.
    /// Atomic, guarded by the internal lock.
    /// </summary>
    /// <remarks>
    /// Intended for modules that react to individual peaks (such as the
    /// <see cref="Modules.AntiClipper"/>) and need the latest value without
    /// walking the whole history.
    /// </remarks>
    public MeterSample? GetLatestSample()
    {
        lock (historyLock)
        {
            if (sampleHistory.Count == 0)
            {
                return null;
            }
            return sampleHistory[sampleHistory.Count - 1];
        }
    }

    /// <summary>
    /// Returns an immutable copy of the current sample history, taken under the
    /// lock. This is the only safe way to iterate the whole history from other
    /// threads while the OSC receive thread appends samples.
    /// </summary>
    /// <remarks>
    /// Iterating the internal list without the lock throws
    /// <see cref="InvalidOperationException"/> if it is modified during
    /// enumeration. Copying under the lock removes the problem; the cost of
    /// copying a few hundred samples is negligible next to how often it runs.
    /// </remarks>
    public List<MeterSample> SnapshotHistory()
    {
        lock (historyLock)
        {
            return new List<MeterSample>(sampleHistory);
        }
    }

    /// <summary>
    /// Records the fader position reported by the console. The first report also
    /// seeds <see cref="LastSentFader"/> so corrections start from the real
    /// position instead of the placeholder 0.0, and flips
    /// <see cref="FaderInitialized"/> to <c>true</c>.
    /// </summary>
    /// <param name="fader">Normalized fader position (0.0 to 1.0) from the console.</param>
    public void UpdateFaderFromConsole(float fader)
    {
        currentFader = fader;
        if (!faderInitialized)
        {
            lastSentFader = fader;
            faderInitialized = true;
        }
    }

    /// <summary>
    /// Records the on/off (mute) state reported by the console. On the M32,
    /// <c>/ch|bus/XX/mix/on</c> is 1 when the device is ON (audible) and 0 when it
    /// is muted, so this stores <see cref="IsMuted"/> as <c>on == 0</c>.
    /// </summary>
    /// <param name="on">The console's on/off value: 1 = on, 0 = muted.</param>
    public void UpdateMuteFromConsole(int on)
    {
        isMuted = on == 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Virtual methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects whether the engineer moved the fader manually since the app last
    /// wrote it.
    /// </summary>
    /// <returns><c>true</c> if <see cref="CurrentFader"/> differs from <see cref="LastSentFader"/> by more than <see cref="FADER_EPSILON"/>.</returns>
    public virtual bool DetectManualChange()
    {
        return Math.Abs(currentFader - lastSentFader) > FADER_EPSILON;
    }

    /// <summary>
    /// Text representation, useful for logs and debugging.
    /// </summary>
    public override string ToString()
    {
        return $"{GetType().Name} #{oscNumber} \"{name}\" target={targetLevelDb:F1}dB muted={isMuted}";
    }
}
