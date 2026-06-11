using System.Collections.Concurrent;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Modules;

/// <summary>
/// Anti-clipping safety mechanism with an adaptive step. Watches the post-trim
/// of channels and reduces the preamp gain when confirmed clipping is detected.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the <see cref="AutoLeveler"/>, the AntiClipper:
/// </para>
/// <list type="bullet">
///   <item>Reacts to <strong>confirmed peaks</strong> (several consecutive
///         samples above the threshold), not to averages.</item>
///   <item>Is <strong>one-directional</strong>: it only attenuates, never boosts.</item>
///   <item>Acts only on channels (<see cref="MixerChannel"/>), not buses.</item>
/// </list>
/// <para>
/// <strong>Adaptive step</strong>: if the overshoot (how far the observed peak
/// exceeds the threshold) is small, it applies the normal step
/// (<c>reductionStepDb</c>); if it is severe, it applies the aggressive step
/// (<c>maxReductionStepDb</c>). It takes advantage of the M32's internal headroom
/// (+18 dBFS) to react before the signal audibly clips on buses and outputs.
/// </para>
/// <para>
/// <strong>Runtime modification</strong>: the channel list can be changed during
/// the show via <see cref="AddChannel"/> and <see cref="RemoveChannel"/>. The
/// list uses a copy-on-write pattern; the per-channel state dictionaries are
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> to allow adds and removes
/// without external locks. The process loop works on an atomic snapshot of the
/// list and uses defensive <c>TryGetValue</c> to tolerate a channel being removed
/// mid-iteration.
/// </para>
/// </remarks>
public class AntiClipper
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Interval between meter reads (50 ms = 20 Hz, the M32's rate).</summary>
    private const int POLL_INTERVAL_MS = 50;

    /// <summary>Sleep granularity inside the polling loop.</summary>
    private const int POLL_SLEEP_TICK_MS = 10;

    /// <summary>Default consecutive samples in clip required to act.</summary>
    public const int DEFAULT_CONFIRMATION_SAMPLES = 2;

    /// <summary>Default cooldown after a reduction, in milliseconds.</summary>
    public const int DEFAULT_COOLDOWN_MS = 2000;

    /// <summary>Default normal step, in dB.</summary>
    public const double DEFAULT_REDUCTION_STEP_DB = 3.0;

    /// <summary>Default aggressive step, in dB.</summary>
    public const double DEFAULT_MAX_REDUCTION_STEP_DB = 6.0;

    /// <summary>
    /// Default overshoot above which the aggressive step is applied. With
    /// clipThresholdDb=-1 and severeOvershootDb=6, the aggressive step fires when
    /// the observed peak exceeds +5 dBFS.
    /// </summary>
    public const double DEFAULT_SEVERE_OVERSHOOT_DB = 6.0;

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private OscClient oscClient;
    private MeterReader meterReader;
    private ShowLogger logger;

    /// <summary>Watched channels, copy-on-write.</summary>
    private volatile List<MixerChannel> channels;

    private double clipThresholdDb;
    private double reductionStepDb;
    private double maxReductionStepDb;
    private double severeOvershootDb;
    private int confirmationSamples;
    private int cooldownMs;

    private volatile bool isEnabled;
    private Thread? processThread;

    /// <summary>Tick of the last logged process-loop error, to throttle error logging.</summary>
    private long lastProcessErrorTick;

    /// <summary>
    /// Mini-history of consecutive clip values per channel. When it fills to
    /// <c>confirmationSamples</c>, a reduction fires using the queue's maximum as
    /// the peak.
    /// </summary>
    private ConcurrentDictionary<MixerChannel, Queue<double>> recentClipsByChannel;

    /// <summary>Time of the last reduction per channel. <c>null</c> = never, or out of cooldown.</summary>
    private ConcurrentDictionary<MixerChannel, DateTime?> lastClipAtByChannel;

    /// <summary>
    /// <c>true</c> while a channel has already been warned that its gain sits at the
    /// console's minimum and the clipping persists. Reset when the channel stops
    /// clipping (or a real reduction is applied again), so the "manual action
    /// required" warning is logged once per clipping episode instead of repeating
    /// every cooldown with no effect.
    /// </summary>
    private ConcurrentDictionary<MixerChannel, bool> minGainWarnedByChannel;

    /// <summary>Timestamp of the last processed sample per channel (de-duplication).</summary>
    private ConcurrentDictionary<MixerChannel, DateTime?> lastSeenTimestampByChannel;

    /// <summary>Per-channel lock to synchronize access to its queue.</summary>
    private ConcurrentDictionary<MixerChannel, object> queueLockByChannel;

    /// <summary>Lock serializing modifications to the channel list.</summary>
    private readonly object channelsLock = new object();

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an AntiClipper, validating every numeric parameter.
    /// </summary>
    public AntiClipper(
        OscClient oscClient,
        MeterReader meterReader,
        ShowLogger logger,
        List<MixerChannel> channels,
        double clipThresholdDb,
        double reductionStepDb = DEFAULT_REDUCTION_STEP_DB,
        double maxReductionStepDb = DEFAULT_MAX_REDUCTION_STEP_DB,
        double severeOvershootDb = DEFAULT_SEVERE_OVERSHOOT_DB,
        int confirmationSamples = DEFAULT_CONFIRMATION_SAMPLES,
        int cooldownMs = DEFAULT_COOLDOWN_MS)
    {
        ValidateClipThreshold(clipThresholdDb);
        ValidateReductionStep(reductionStepDb, maxReductionStepDb);
        ValidateMaxReductionStep(maxReductionStepDb, reductionStepDb);
        ValidateSevereOvershoot(severeOvershootDb);
        ValidateConfirmationSamples(confirmationSamples);
        ValidateCooldown(cooldownMs);

        this.oscClient = oscClient;
        this.meterReader = meterReader;
        this.logger = logger;
        this.channels = new List<MixerChannel>(channels);
        this.clipThresholdDb = clipThresholdDb;
        this.reductionStepDb = reductionStepDb;
        this.maxReductionStepDb = maxReductionStepDb;
        this.severeOvershootDb = severeOvershootDb;
        this.confirmationSamples = confirmationSamples;
        this.cooldownMs = cooldownMs;
        this.isEnabled = false;
        this.processThread = null;

        this.recentClipsByChannel = new ConcurrentDictionary<MixerChannel, Queue<double>>();
        this.lastClipAtByChannel = new ConcurrentDictionary<MixerChannel, DateTime?>();
        this.lastSeenTimestampByChannel = new ConcurrentDictionary<MixerChannel, DateTime?>();
        this.queueLockByChannel = new ConcurrentDictionary<MixerChannel, object>();
        this.minGainWarnedByChannel = new ConcurrentDictionary<MixerChannel, bool>();

        foreach (MixerChannel channel in channels)
        {
            InitializeChannelState(channel);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    public IReadOnlyList<MixerChannel> Channels
    {
        get { return channels.AsReadOnly(); }
    }

    /// <remarks>
    /// The setters apply the same validation as the constructor, so a live edit
    /// can never put the module in a state the constructor would have rejected.
    /// </remarks>
    public double ClipThresholdDb
    {
        get { return clipThresholdDb; }
        set { ValidateClipThreshold(value); clipThresholdDb = value; }
    }

    public double ReductionStepDb
    {
        get { return reductionStepDb; }
        set { ValidateReductionStep(value, maxReductionStepDb); reductionStepDb = value; }
    }

    public double MaxReductionStepDb
    {
        get { return maxReductionStepDb; }
        set { ValidateMaxReductionStep(value, reductionStepDb); maxReductionStepDb = value; }
    }

    public double SevereOvershootDb
    {
        get { return severeOvershootDb; }
        set { ValidateSevereOvershoot(value); severeOvershootDb = value; }
    }

    public int ConfirmationSamples
    {
        get { return confirmationSamples; }
        set { ValidateConfirmationSamples(value); confirmationSamples = value; }
    }

    public int CooldownMs
    {
        get { return cooldownMs; }
        set { ValidateCooldown(value); cooldownMs = value; }
    }

    public bool IsEnabled
    {
        get { return isEnabled; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Parameter validation (shared by the constructor and the setters)
    // ─────────────────────────────────────────────────────────────────────

    private static void ValidateClipThreshold(double clipThresholdDb)
    {
        if (clipThresholdDb > 0 || clipThresholdDb < -20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clipThresholdDb),
                "Clip threshold must be between -20 and 0 dB.");
        }
    }

    private static void ValidateReductionStep(double reductionStepDb, double maxReductionStepDb)
    {
        if (reductionStepDb < 0.1 || reductionStepDb > 12 || reductionStepDb > maxReductionStepDb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reductionStepDb),
                "Normal reduction step must be between 0.1 and 12 dB and no greater than the aggressive step.");
        }
    }

    private static void ValidateMaxReductionStep(double maxReductionStepDb, double reductionStepDb)
    {
        if (maxReductionStepDb < reductionStepDb || maxReductionStepDb > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxReductionStepDb),
                "Aggressive step must be >= normal step and no greater than 12 dB.");
        }
    }

    private static void ValidateSevereOvershoot(double severeOvershootDb)
    {
        if (severeOvershootDb < 1 || severeOvershootDb > 18)
        {
            throw new ArgumentOutOfRangeException(
                nameof(severeOvershootDb),
                "Severe-overshoot threshold must be between 1 and 18 dB.");
        }
    }

    private static void ValidateConfirmationSamples(int confirmationSamples)
    {
        if (confirmationSamples < 1 || confirmationSamples > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmationSamples),
                "Confirmation samples must be between 1 and 20.");
        }
    }

    private static void ValidateCooldown(int cooldownMs)
    {
        if (cooldownMs < 100 || cooldownMs > 30000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cooldownMs),
                "Cooldown must be between 100 ms and 30 s.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Runtime modification of the channel list
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a channel to the watched set, initializes its internal state and logs
    /// the event. Does nothing if the channel is already present.
    /// </summary>
    public void AddChannel(MixerChannel channel)
    {
        lock (channelsLock)
        {
            if (channels.Contains(channel))
            {
                return;
            }

            List<MixerChannel> updated = new List<MixerChannel>(channels);
            updated.Add(channel);
            channels = updated;

            InitializeChannelState(channel);
        }

        logger.LogEvent(
            $"AntiClipper|system|channel_added|{channel.Name}",
            Severity.Info);
    }

    /// <summary>
    /// Removes a channel from the watched set and frees its internal state. Does
    /// nothing if the channel is not present.
    /// </summary>
    public void RemoveChannel(MixerChannel channel)
    {
        lock (channelsLock)
        {
            if (!channels.Contains(channel))
            {
                return;
            }

            List<MixerChannel> updated = new List<MixerChannel>(channels);
            updated.Remove(channel);
            channels = updated;

            recentClipsByChannel.TryRemove(channel, out _);
            lastClipAtByChannel.TryRemove(channel, out _);
            lastSeenTimestampByChannel.TryRemove(channel, out _);
            queueLockByChannel.TryRemove(channel, out _);
            minGainWarnedByChannel.TryRemove(channel, out _);
        }

        logger.LogEvent(
            $"AntiClipper|system|channel_removed|{channel.Name}",
            Severity.Info);
    }

    /// <summary>
    /// Initializes the tracking state of a newly added channel.
    /// </summary>
    private void InitializeChannelState(MixerChannel channel)
    {
        recentClipsByChannel[channel] = new Queue<double>(confirmationSamples);
        lastClipAtByChannel[channel] = null;
        lastSeenTimestampByChannel[channel] = null;
        queueLockByChannel[channel] = new object();
        minGainWarnedByChannel[channel] = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables the module and starts the polling thread.
    /// </summary>
    public void Monitor()
    {
        if (isEnabled)
        {
            return;
        }

        isEnabled = true;

        processThread = new Thread(ProcessLoop);
        processThread.IsBackground = true;
        processThread.Name = "AntiClipper-Process";
        processThread.Start();

        logger.LogEvent("AntiClipper|system|enabled", Severity.Info);
    }

    /// <summary>
    /// Disables the module. Applied reductions are not reverted.
    /// </summary>
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

        logger.LogEvent("AntiClipper|system|disabled", Severity.Info);
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
                // Atomic snapshot of the list: if it changes mid-iteration, this
                // cycle finishes on the old version.
                List<MixerChannel> snapshot = channels;

                foreach (MixerChannel channel in snapshot)
                {
                    EvaluateChannel(channel);
                }
            }
            catch (Exception ex)
            {
                // A clip check must never kill the module mid-show: log (throttled)
                // and keep running.
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
    /// Evaluates a channel: gets its latest sample, checks whether it is new, and
    /// if so analyzes whether it is clipping and whether to act.
    /// </summary>
    private void EvaluateChannel(MixerChannel channel)
    {
        // A muted channel is not audible, so never attenuate its preamp even if its
        // pre-fader input is still clipping.
        if (channel.IsMuted)
        {
            ClearClipHistory(channel);
            return;
        }

        MeterSample? sample = meterReader.GetLatestSample(channel);

        if (sample == null)
        {
            return;
        }

        // The channel might have been removed between the snapshot and here:
        // defensive TryGetValue. If it is gone, skip this cycle.
        if (!lastSeenTimestampByChannel.TryGetValue(channel, out DateTime? lastSeen))
        {
            return;
        }

        if (lastSeen != null && sample.Timestamp <= lastSeen.Value)
        {
            return;
        }
        lastSeenTimestampByChannel[channel] = sample.Timestamp;

        double postTrimDb = sample.PostTrimDb;
        if (double.IsNaN(postTrimDb))
        {
            return;
        }

        if (IsInCooldown(channel))
        {
            ClearClipHistory(channel);
            return;
        }

        if (postTrimDb >= clipThresholdDb)
        {
            int currentCount = AddClipSample(channel, postTrimDb);

            if (currentCount >= confirmationSamples)
            {
                double peakDb = GetPeakAndClearHistory(channel);
                ApplyAdaptiveReduction(channel, peakDb);
            }
        }
        else
        {
            // The clipping episode is over: clear the confirmation history and
            // re-arm the at-minimum warning for the next episode.
            ClearClipHistory(channel);
            minGainWarnedByChannel[channel] = false;
        }
    }

    /// <summary>
    /// Adds a clip sample to the channel's history and returns the number of
    /// stored samples. If the queue exceeds its maximum size, the oldest is
    /// dropped.
    /// </summary>
    private int AddClipSample(MixerChannel channel, double clipDb)
    {
        if (!queueLockByChannel.TryGetValue(channel, out object? lockObj))
        {
            return 0;
        }
        if (!recentClipsByChannel.TryGetValue(channel, out Queue<double>? queue))
        {
            return 0;
        }

        lock (lockObj)
        {
            queue.Enqueue(clipDb);
            while (queue.Count > confirmationSamples)
            {
                queue.Dequeue();
            }
            return queue.Count;
        }
    }

    /// <summary>
    /// Returns the maximum of the channel's clip history and empties it
    /// atomically.
    /// </summary>
    private double GetPeakAndClearHistory(MixerChannel channel)
    {
        if (!queueLockByChannel.TryGetValue(channel, out object? lockObj))
        {
            return double.MinValue;
        }
        if (!recentClipsByChannel.TryGetValue(channel, out Queue<double>? queue))
        {
            return double.MinValue;
        }

        lock (lockObj)
        {
            double peak = double.MinValue;
            foreach (double db in queue)
            {
                if (db > peak)
                {
                    peak = db;
                }
            }
            queue.Clear();
            return peak;
        }
    }

    /// <summary>
    /// Empties a channel's clip history atomically.
    /// </summary>
    private void ClearClipHistory(MixerChannel channel)
    {
        if (!queueLockByChannel.TryGetValue(channel, out object? lockObj))
        {
            return;
        }
        if (!recentClipsByChannel.TryGetValue(channel, out Queue<double>? queue))
        {
            return;
        }

        lock (lockObj)
        {
            queue.Clear();
        }
    }

    /// <summary>Window (ms) during which a channel counts as "recently reduced" for the UI.</summary>
    public const int RECENT_REDUCTION_HIGHLIGHT_MS = 2500;

    /// <summary>
    /// <c>true</c> if this channel's gain was reduced within the last
    /// <see cref="RECENT_REDUCTION_HIGHLIGHT_MS"/> ms, so the dashboard can highlight it.
    /// </summary>
    public bool WasRecentlyReduced(MixerChannel channel)
    {
        if (lastClipAtByChannel.TryGetValue(channel, out DateTime? last) && last != null)
        {
            return (DateTime.UtcNow - last.Value).TotalMilliseconds < RECENT_REDUCTION_HIGHLIGHT_MS;
        }
        return false;
    }

    private bool IsInCooldown(MixerChannel channel)
    {
        if (!lastClipAtByChannel.TryGetValue(channel, out DateTime? lastClipAt))
        {
            return false;
        }
        if (lastClipAt == null)
        {
            return false;
        }

        double elapsedMs = (DateTime.UtcNow - lastClipAt.Value).TotalMilliseconds;
        return elapsedMs < cooldownMs;
    }

    /// <summary>
    /// Applies the reduction with an adaptive step: normal step if the overshoot
    /// is small, aggressive step if it is severe. The resulting gain is clamped to
    /// the console's physical minimum and quantized to its gain grid (0.5 dB steps
    /// for a head amp, 0.25 dB for the digital trim), so the stored value, the log
    /// and the console agree from the first write. If the gain already sits at the
    /// minimum, no redundant write is sent; instead a single "manual action
    /// required" warning is logged per clipping episode.
    /// </summary>
    private void ApplyAdaptiveReduction(MixerChannel channel, double peakClipDb)
    {
        bool hasHeadAmp = channel.HeadAmpIndex >= 0;
        double minGain = hasHeadAmp
            ? M32Buckets.HEAD_AMP_GAIN_MIN_DB
            : M32Buckets.CHANNEL_TRIM_MIN_DB;
        double gainStep = hasHeadAmp
            ? M32Buckets.HEAD_AMP_GAIN_STEP_DB
            : M32Buckets.CHANNEL_TRIM_STEP_DB;

        double previousGain = channel.PreampGain;

        // Already at the console's minimum: a further reduction is impossible.
        // Re-sending the same value and logging "reductions" that reduce nothing
        // would only flood the dashboard; warn once per episode that the engineer
        // has to act (back the source off, pad the mic, swap the DI).
        if (previousGain <= minGain)
        {
            if (minGainWarnedByChannel.TryGetValue(channel, out bool warned) && !warned)
            {
                minGainWarnedByChannel[channel] = true;
                logger.LogEvent(
                    $"AntiClipper|{channel.Name}|at minimum gain ({minGain:F1}dB) and still clipping" +
                    $"|peak={peakClipDb:+0.0;-0.0}dBFS|manual action required",
                    Severity.Warning);
            }
            lastClipAtByChannel[channel] = DateTime.UtcNow;
            return;
        }

        double overshootDb = peakClipDb - clipThresholdDb;

        double stepToApply;
        bool isSevere;

        if (overshootDb >= severeOvershootDb)
        {
            stepToApply = maxReductionStepDb;
            isSevere = true;
        }
        else
        {
            stepToApply = reductionStepDb;
            isSevere = false;
        }

        channel.ReducePreampGain(stepToApply);

        // Clamp to the physical minimum the console enforces and quantize to its
        // gain grid: the console rounds any in-between value anyway, so storing
        // the rounded value keeps the local state identical to what the console
        // will echo back.
        double newGain = Math.Max(channel.PreampGain, minGain);
        newGain = Math.Round(newGain / gainStep) * gainStep;
        channel.PreampGain = Math.Max(newGain, minGain);

        minGainWarnedByChannel[channel] = false;
        SendPreampUpdate(channel);
        lastClipAtByChannel[channel] = DateTime.UtcNow;

        string eventTag = isSevere ? "severe_reduction" : "reduction";

        logger.LogEvent(
            $"AntiClipper|{channel.Name}|{eventTag}|" +
            $"{previousGain:F1}dB|{channel.PreampGain:F1}dB|" +
            $"peak={peakClipDb:+0.0;-0.0}dBFS|overshoot={overshootDb:F1}dB|step={stepToApply:F1}dB",
            Severity.Warning);
    }

    /// <summary>
    /// Sends the channel's new preamp gain to the console via OSC, normalized to
    /// the [0,1] range the console expects. If the console reported a head amp
    /// feeding this channel (local XLR or AES50 stage box), it writes
    /// <c>/headamp/NNN/gain</c>; otherwise (digital source, no head amp) it falls
    /// back to the channel's digital trim <c>/ch/NN/preamp/trim</c>.
    /// </summary>
    private void SendPreampUpdate(MixerChannel channel)
    {
        string oscAddress;
        float normalized;

        if (channel.HeadAmpIndex >= 0)
        {
            oscAddress = M32Buckets.BuildHeadAmpGainAddress(channel.HeadAmpIndex);
            normalized = FaderMath.Normalize(
                channel.PreampGain, M32Buckets.HEAD_AMP_GAIN_MIN_DB, M32Buckets.HEAD_AMP_GAIN_MAX_DB);
        }
        else
        {
            oscAddress = M32Buckets.BuildChannelTrimAddress(channel.OscNumber);
            normalized = FaderMath.Normalize(
                channel.PreampGain, M32Buckets.CHANNEL_TRIM_MIN_DB, M32Buckets.CHANNEL_TRIM_MAX_DB);
        }

        OscMessage update = new OscMessage(oscAddress, ",f", new List<object> { normalized });
        oscClient.Send(update);
    }

    /// <summary>Logs a process-loop error at most once every few seconds.</summary>
    private void LogProcessErrorThrottled(Exception ex)
    {
        long now = Environment.TickCount64;
        if (now - lastProcessErrorTick > 5000)
        {
            lastProcessErrorTick = now;
            logger.LogEvent($"AntiClipper|process error|{ex.Message}", Severity.Warning);
        }
    }
}
