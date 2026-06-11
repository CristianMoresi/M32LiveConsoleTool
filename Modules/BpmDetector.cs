using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Modules;

/// <summary>
/// Tempo (BPM) detector based on a rhythmic reference channel. Supports three
/// modes (<see cref="BpmDetectionMode"/>) and applies temporal hysteresis with a
/// dual window to tell real tempo changes from one-off atypical events (fills,
/// breakdowns, spurious transients).
/// </summary>
/// <remarks>
/// <para>
/// The detector polls the <see cref="MeterReader"/> on its own thread, applying
/// edge detection on the target channel's post-trim. It also supports manual tap
/// tempo via <see cref="TapTempo"/>.
/// </para>
/// <para>
/// <strong>Dual window</strong>: two rolling windows of transient timestamps feed
/// the estimate. The <em>short</em> window (8 transients) reacts quickly and drives
/// change detection; the <em>long</em> window (32 transients) is a slow, stable
/// reference. Each cycle takes the median of the inter-transient intervals (robust
/// against a missed or spurious hit) of each window. When the tempo is steady, the
/// established BPM is refined toward the long window's median, so it settles and
/// stops jittering; when the short window reports a clearly different tempo for
/// several consecutive detections, a real change is accepted (see
/// <see cref="TryUpdateBpm"/>). This balances fast reaction to song changes against
/// a rock-steady reading on a constant tempo.
/// </para>
/// <para>
/// <strong>Persistence</strong>: once the first valid BPM is computed, it is kept
/// as the reference indefinitely. It is only overwritten by an accepted new one;
/// there is no "invalidation on timeout".
/// </para>
/// <para>
/// <strong>Runtime modification</strong>: the <see cref="TargetChannel"/> and
/// <see cref="Mode"/> setters reset the detection state (windows and edge state)
/// so the detector starts fresh with the new configuration.
/// </para>
/// </remarks>
public class BpmDetector
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    private const int POLL_INTERVAL_MS = 50;
    private const int POLL_SLEEP_TICK_MS = 10;

    private const double MIN_REASONABLE_BPM = 60.0;
    private const double MAX_REASONABLE_BPM = 180.0;
    private const double ABSOLUTE_MIN_BPM = 40.0;
    private const double ABSOLUTE_MAX_BPM = 300.0;

    /// <summary>
    /// Size of the short window (transients). Few enough to react within a couple of
    /// bars to a real tempo change; drives change detection.
    /// </summary>
    private const int SHORT_WINDOW_SIZE = 8;

    /// <summary>
    /// Size of the long window (transients). Large enough that its median is a stable
    /// reference the established tempo is refined toward while the tempo holds.
    /// </summary>
    private const int LONG_WINDOW_SIZE = 32;

    /// <summary>
    /// Gap (ms) between consecutive transients above which both windows are cleared
    /// and detection restarts. A gap this long is a pause (it implies a tempo below
    /// the absolute minimum), so the pre-pause transients must not distort the median
    /// when the music resumes — possibly at a different tempo.
    /// </summary>
    private const double TRANSIENT_GAP_RESET_MS = 4000.0;

    /// <summary>Margin in BPM below which a difference is treated as refinement.</summary>
    private const double REFINE_RANGE_BPM = 5.0;

    /// <summary>
    /// Consecutive similar detections of a "very different" BPM required before
    /// accepting it as a real change.
    /// </summary>
    private const int CHANGE_CONFIRMATION_COUNT = 4;

    /// <summary>Tolerance for treating two candidates as "the same change".</summary>
    private const double CANDIDATE_MATCH_TOLERANCE_BPM = 3.0;

    /// <summary>Default hysteresis before notifying the DelayController.</summary>
    public const double DEFAULT_BPM_CHANGE_HYSTERESIS = 1.0;

    /// <summary>Weight of the candidate in the weighted refinement.</summary>
    private const double REFINE_BLEND_FACTOR = 0.3;

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private MeterReader meterReader;
    private DelayController delayController;
    private ShowLogger logger;

    private MixerChannel targetChannel;
    private BpmDetectionMode mode;

    private double transientThresholdDb;
    private double bpmChangeHysteresis;

    private volatile bool isEnabled;
    private Thread? processThread;

    /// <summary>Tick of the last logged process-loop error, to throttle error logging.</summary>
    private long lastProcessErrorTick;

    /// <summary>Short window of transient timestamps (fast reaction, change detection).</summary>
    private List<DateTime> shortWindow;

    /// <summary>Long window of transient timestamps (stable established-tempo reference).</summary>
    private List<DateTime> longWindow;

    /// <summary>Lock protecting both windows.</summary>
    private readonly object windowLock = new object();

    /// <summary>Timestamp of the last processed sample (de-duplication).</summary>
    private DateTime? lastSeenTimestamp;

    /// <summary>Edge-detection state.</summary>
    private bool wasAboveThreshold;

    /// <summary>Currently established BPM. 0 until the first one is computed.</summary>
    private double currentBpm;

    /// <summary>Whether a valid BPM has ever been computed this session.</summary>
    private volatile bool hasEverBeenValid;

    /// <summary>UTC time of the most recent detected transient or tap, for the freshness display.</summary>
    private DateTime lastTransientUtc;

    /// <summary>Last BPM notified to the DelayController.</summary>
    private double lastNotifiedBpm;

    /// <summary>Candidate BPM pending confirmation.</summary>
    private double candidateBpm;

    /// <summary>Consecutive detections of the current candidate.</summary>
    private int candidateMatchCount;

    /// <summary>
    /// Timestamp of the newest transient already counted as change-confirmation
    /// evidence. The poll loop re-evaluates the same windows every cycle (50 ms);
    /// without this guard, re-reading an unchanged window four times would satisfy
    /// the confirmation count in 200 ms with no new musical evidence at all.
    /// </summary>
    private DateTime lastChangeEvidenceUtc = DateTime.MinValue;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a BPM detector with its parameters validated.
    /// </summary>
    public BpmDetector(
        MeterReader meterReader,
        DelayController delayController,
        ShowLogger logger,
        MixerChannel targetChannel,
        BpmDetectionMode mode,
        double transientThresholdDb,
        double bpmChangeHysteresis = DEFAULT_BPM_CHANGE_HYSTERESIS)
    {
        ValidateTransientThreshold(transientThresholdDb);
        ValidateHysteresis(bpmChangeHysteresis);

        this.meterReader = meterReader;
        this.delayController = delayController;
        this.logger = logger;
        this.targetChannel = targetChannel;
        this.mode = mode;
        this.transientThresholdDb = transientThresholdDb;
        this.bpmChangeHysteresis = bpmChangeHysteresis;
        this.isEnabled = false;
        this.processThread = null;

        this.shortWindow = new List<DateTime>();
        this.longWindow = new List<DateTime>();
        this.lastSeenTimestamp = null;
        this.wasAboveThreshold = false;
        this.currentBpm = 0.0;
        this.hasEverBeenValid = false;
        this.lastTransientUtc = DateTime.MinValue;
        this.lastNotifiedBpm = 0.0;
        this.candidateBpm = 0.0;
        this.candidateMatchCount = 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reference channel transients are read from. Changing it resets the
    /// edge-detection state and clears the windows: the detector starts fresh on
    /// the new channel, with no carryover from the previous one.
    /// </summary>
    public MixerChannel TargetChannel
    {
        get { return targetChannel; }
        set
        {
            if (ReferenceEquals(targetChannel, value))
            {
                return;
            }

            targetChannel = value;

            lock (windowLock)
            {
                shortWindow.Clear();
                longWindow.Clear();
            }
            lastSeenTimestamp = null;
            wasAboveThreshold = false;
            ResetCandidate();

            logger.LogEvent(
                $"BpmDetector|system|target_channel_changed|{value.Name}",
                Severity.Info);
        }
    }

    /// <summary>
    /// Operating mode. Changing it clears the windows because the interpretation
    /// of accumulated transients may not be valid in the new mode.
    /// </summary>
    public BpmDetectionMode Mode
    {
        get { return mode; }
        set
        {
            if (mode == value)
            {
                return;
            }

            BpmDetectionMode previous = mode;
            mode = value;

            lock (windowLock)
            {
                shortWindow.Clear();
                longWindow.Clear();
            }
            lastSeenTimestamp = null;
            wasAboveThreshold = false;
            ResetCandidate();

            logger.LogEvent(
                $"BpmDetector|system|mode_changed|from={previous}|to={value}",
                Severity.Info);
        }
    }

    /// <remarks>
    /// The setters apply the same validation as the constructor, so a live edit
    /// can never put the module in a state the constructor would have rejected.
    /// </remarks>
    public double TransientThresholdDb
    {
        get { return transientThresholdDb; }
        set { ValidateTransientThreshold(value); transientThresholdDb = value; }
    }

    public double BpmChangeHysteresis
    {
        get { return bpmChangeHysteresis; }
        set { ValidateHysteresis(value); bpmChangeHysteresis = value; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Parameter validation (shared by the constructor and the setters)
    // ─────────────────────────────────────────────────────────────────────

    private static void ValidateTransientThreshold(double transientThresholdDb)
    {
        if (transientThresholdDb > 0 || transientThresholdDb < -60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transientThresholdDb),
                "Transient threshold must be between -60 and 0 dB.");
        }
    }

    private static void ValidateHysteresis(double bpmChangeHysteresis)
    {
        if (bpmChangeHysteresis < 0 || bpmChangeHysteresis > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bpmChangeHysteresis),
                "Hysteresis must be between 0 and 20 BPM.");
        }
    }

    /// <summary>
    /// Established BPM. 0 until the first one is computed. After that it keeps its
    /// last valid value even through silences.
    /// </summary>
    public double CurrentBpm
    {
        get { return currentBpm; }
    }

    /// <summary>
    /// <c>true</c> as soon as the first valid BPM of the session is computed.
    /// </summary>
    public bool HasEverBeenValid
    {
        get { return hasEverBeenValid; }
    }

    /// <summary>
    /// Milliseconds since the last detected transient (or manual tap). The dashboard
    /// uses it to flag a stale reading — when the music stops and no new transients
    /// arrive, the established BPM is kept but shown as no longer live. 0 if no BPM
    /// has been computed yet.
    /// </summary>
    public double LastBpmAgeMs
    {
        get
        {
            if (!hasEverBeenValid)
            {
                return 0.0;
            }
            return (DateTime.UtcNow - lastTransientUtc).TotalMilliseconds;
        }
    }

    public bool IsEnabled
    {
        get { return isEnabled; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (isEnabled)
        {
            return;
        }

        isEnabled = true;

        processThread = new Thread(ProcessLoop);
        processThread.IsBackground = true;
        processThread.Name = "BpmDetector-Process";
        processThread.Start();

        logger.LogEvent($"BpmDetector|system|enabled|mode={mode}", Severity.Info);
    }

    public void Stop()
    {
        if (!isEnabled)
        {
            return;
        }

        isEnabled = false;

        if (processThread != null && processThread.IsAlive)
        {
            processThread.Join(1000);
            processThread = null;
        }

        logger.LogEvent("BpmDetector|system|disabled", Severity.Info);
    }

    /// <summary>
    /// Records a manual tap from the engineer. Useful in any mode, but it is the
    /// only source of transients in <see cref="BpmDetectionMode.ManualTap"/>.
    /// </summary>
    public void TapTempo()
    {
        RegisterTransient(DateTime.UtcNow);
    }

    /// <summary>
    /// Records a transient (a tap or a detected edge) into both windows. If the gap
    /// since the previous transient is longer than <see cref="TRANSIENT_GAP_RESET_MS"/>
    /// the windows are cleared first, so the detector restarts cleanly after a pause
    /// instead of carrying a huge gap-interval into the median.
    /// </summary>
    private void RegisterTransient(DateTime t)
    {
        lock (windowLock)
        {
            if (shortWindow.Count > 0)
            {
                DateTime last = shortWindow[shortWindow.Count - 1];
                if ((t - last).TotalMilliseconds > TRANSIENT_GAP_RESET_MS)
                {
                    shortWindow.Clear();
                    longWindow.Clear();
                }
            }

            shortWindow.Add(t);
            longWindow.Add(t);
            TrimWindows();
        }

        lastTransientUtc = t;
    }

    /// <summary>
    /// Returns the time in milliseconds for each rhythmic figure at the
    /// established BPM. Empty dictionary if there is no valid BPM.
    /// </summary>
    public Dictionary<BeatType, double> GetDelayTimes()
    {
        Dictionary<BeatType, double> result = new Dictionary<BeatType, double>();

        if (!hasEverBeenValid || currentBpm <= 0)
        {
            return result;
        }

        foreach (BeatType beat in Enum.GetValues<BeatType>())
        {
            result[beat] = FaderMath.DelayMsForBeat(currentBpm, beat);
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Process loop
    // ─────────────────────────────────────────────────────────────────────

    private void ProcessLoop()
    {
        while (isEnabled)
        {
            try
            {
                // In Click and BeatTracking, read from the channel. In ManualTap,
                // transients come only from TapTempo() invoked externally.
                if (mode != BpmDetectionMode.ManualTap)
                {
                    DetectTransientsFromChannel();
                }

                TryUpdateBpm();
            }
            catch (Exception ex)
            {
                // Tempo analysis must never kill the module mid-show: log
                // (throttled) and keep running.
                LogProcessErrorThrottled(ex);
            }

            int slept = 0;
            while (slept < POLL_INTERVAL_MS && isEnabled)
            {
                Thread.Sleep(POLL_SLEEP_TICK_MS);
                slept += POLL_SLEEP_TICK_MS;
            }
        }
    }

    /// <summary>
    /// Reads the target channel's latest sample and applies edge detection to add
    /// a transient if appropriate.
    /// </summary>
    private void DetectTransientsFromChannel()
    {
        MeterSample? sample = meterReader.GetLatestSample(targetChannel);

        if (sample == null)
        {
            return;
        }

        if (lastSeenTimestamp != null && sample.Timestamp <= lastSeenTimestamp.Value)
        {
            return;
        }
        lastSeenTimestamp = sample.Timestamp;

        double postTrimDb = sample.PostTrimDb;
        if (double.IsNaN(postTrimDb))
        {
            return;
        }

        bool isAboveThreshold = postTrimDb >= transientThresholdDb;

        // Edge detection: only register if the previous sample was below and this
        // one crosses upward.
        if (isAboveThreshold && !wasAboveThreshold)
        {
            RegisterTransient(sample.Timestamp);
        }

        wasAboveThreshold = isAboveThreshold;
    }

    /// <summary>
    /// Computes a candidate BPM from the short window (fast reaction) and decides
    /// whether to accept it based on how close or far it is from the established BPM.
    /// On a steady tempo it refines toward the long window's median (stability); a
    /// clearly different reading must be confirmed several times before it is taken
    /// as a real change.
    /// </summary>
    private void TryUpdateBpm()
    {
        double? candidate = ComputeBpmCandidate(shortWindow);

        if (candidate == null)
        {
            return;
        }

        double newBpm = candidate.Value;

        // Case 1: there was never a valid BPM. Accept the short window's first reading
        // so the detector locks on quickly.
        if (!hasEverBeenValid)
        {
            CommitNewBpm(newBpm, "first_bpm");
            return;
        }

        double diff = Math.Abs(newBpm - currentBpm);

        // Case 2: steady tempo. Refine toward the LONG window's median, which averages
        // far more transients and is much steadier than the short one, so the
        // established BPM settles instead of jittering. Fall back to the short
        // candidate until the long window has gathered enough intervals.
        if (diff < REFINE_RANGE_BPM)
        {
            double stableTarget = ComputeBpmCandidate(longWindow) ?? newBpm;
            double blended = currentBpm * (1.0 - REFINE_BLEND_FACTOR)
                           + stableTarget * REFINE_BLEND_FACTOR;
            CommitNewBpm(blended, "refinement");
            ResetCandidate();
            return;
        }

        // Case 3: very different candidate. Possible song change, but require
        // several consecutive similar detections before accepting — and only count
        // a detection when a NEW transient has arrived since the last one counted,
        // so the confirmation reflects real musical evidence rather than the poll
        // loop re-reading the same window every 50 ms.
        if (lastTransientUtc <= lastChangeEvidenceUtc)
        {
            return;
        }
        lastChangeEvidenceUtc = lastTransientUtc;

        if (Math.Abs(newBpm - candidateBpm) < CANDIDATE_MATCH_TOLERANCE_BPM
            && candidateBpm > 0)
        {
            candidateMatchCount++;

            if (candidateMatchCount >= CHANGE_CONFIRMATION_COUNT)
            {
                CommitNewBpm(newBpm, "confirmed_change");
                ResetCandidate();
            }
        }
        else
        {
            // New candidate: start the counter.
            candidateBpm = newBpm;
            candidateMatchCount = 1;
        }
    }

    /// <summary>
    /// Computes the raw BPM from the given window's transients. <c>null</c> if there
    /// is not enough data. Applies octave correction in BeatTracking.
    /// </summary>
    private double? ComputeBpmCandidate(List<DateTime> window)
    {
        List<double> intervalsMs = ExtractIntervalsMs(window);

        // Manual tap locks on with a single interval (two taps), as a tap-tempo
        // button is expected to. Automatic modes need at least two intervals so the
        // median has something to reject an outlier with.
        int minIntervals = mode == BpmDetectionMode.ManualTap ? 1 : 2;
        if (intervalsMs.Count < minIntervals)
        {
            return null;
        }

        double medianMs = Median(intervalsMs);

        if (medianMs <= 0)
        {
            return null;
        }

        double rawBpm = 60000.0 / medianMs;

        if (mode == BpmDetectionMode.Click || mode == BpmDetectionMode.ManualTap)
        {
            if (IsAbsolutelyOutOfRange(rawBpm))
            {
                return null;
            }
            return rawBpm;
        }

        // BeatTracking: apply octave correction if the raw value falls outside the
        // reasonable range but its double or half lands inside.
        return AdjustBpmOctave(rawBpm);
    }

    private List<double> ExtractIntervalsMs(List<DateTime> window)
    {
        List<double> result = new List<double>();

        lock (windowLock)
        {
            for (int i = 1; i < window.Count; i++)
            {
                double ms = (window[i] - window[i - 1]).TotalMilliseconds;
                if (ms > 0)
                {
                    result.Add(ms);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Computes the median of a list of values. More robust than the mean against
    /// outliers (spurious or missed transients).
    /// </summary>
    private double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        List<double> sorted = new List<double>(values);
        sorted.Sort();

        int n = sorted.Count;
        if (n % 2 == 1)
        {
            return sorted[n / 2];
        }
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>
    /// Adjusts a raw BPM to the reasonable range by trying its double or half if
    /// the raw value falls outside. Handles beat tracking on sources that mark
    /// subdivisions other than the quarter note.
    /// </summary>
    private double? AdjustBpmOctave(double rawBpm)
    {
        if (IsAbsolutelyOutOfRange(rawBpm))
        {
            return null;
        }

        if (IsInReasonableRange(rawBpm))
        {
            return rawBpm;
        }

        if (IsInReasonableRange(rawBpm * 2.0))
        {
            return rawBpm * 2.0;
        }

        if (IsInReasonableRange(rawBpm / 2.0))
        {
            return rawBpm / 2.0;
        }

        return rawBpm;
    }

    private bool IsInReasonableRange(double bpm)
    {
        return bpm >= MIN_REASONABLE_BPM && bpm <= MAX_REASONABLE_BPM;
    }

    private bool IsAbsolutelyOutOfRange(double bpm)
    {
        return bpm < ABSOLUTE_MIN_BPM || bpm > ABSOLUTE_MAX_BPM;
    }

    /// <summary>
    /// Accepts a new BPM as established and notifies the DelayController if it
    /// exceeds the configured hysteresis.
    /// </summary>
    private void CommitNewBpm(double newBpm, string reason)
    {
        currentBpm = newBpm;
        hasEverBeenValid = true;

        if (Math.Abs(newBpm - lastNotifiedBpm) >= bpmChangeHysteresis)
        {
            lastNotifiedBpm = newBpm;
            delayController.ApplyAllFromBpm(newBpm);
            logger.LogEvent(
                $"BpmDetector|system|bpm_updated|{newBpm:F1}|reason={reason}",
                Severity.Info);
        }
    }

    private void ResetCandidate()
    {
        candidateBpm = 0.0;
        candidateMatchCount = 0;
    }

    /// <summary>
    /// Trims both windows to their maximum size. Must be called from inside a lock on
    /// <see cref="windowLock"/>.
    /// </summary>
    private void TrimWindows()
    {
        while (shortWindow.Count > SHORT_WINDOW_SIZE)
        {
            shortWindow.RemoveAt(0);
        }
        while (longWindow.Count > LONG_WINDOW_SIZE)
        {
            longWindow.RemoveAt(0);
        }
    }

    /// <summary>Logs a process-loop error at most once every few seconds.</summary>
    private void LogProcessErrorThrottled(Exception ex)
    {
        long now = Environment.TickCount64;
        if (now - lastProcessErrorTick > 5000)
        {
            lastProcessErrorTick = now;
            logger.LogEvent($"BpmDetector|process error|{ex.Message}", Severity.Warning);
        }
    }
}
