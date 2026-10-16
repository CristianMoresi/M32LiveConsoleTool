using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Domain;

/// <summary>Observed console state and a bounded, sequenced meter history.</summary>
public abstract class AudioDevice
{
    public const float FADER_EPSILON = 0.001f;
    public const int METER_MAX_AGE_MS = 1000;
    public const int HISTORY_RETENTION_MS = 11000;
    private const int MAX_HISTORY_SAMPLES = 4096;
    private const int ECHO_WINDOW_MS = 2000;
    protected string name;
    protected int oscNumber;
    protected double targetLevelDb;
    protected volatile float lastSentFader, currentFader;
    protected volatile bool faderInitialized, isMuted;
    private volatile bool muteInitialized;
    private bool manualChange;
    private readonly Queue<(long Sequence, MeterSample Sample)> history = new();
    private readonly Queue<(float Value, long Tick)> sentFaders = new();
    private readonly object historyLock = new();
    private readonly object stateLock = new();
    private long sequence;
    private MeterSample? latest;
    private long? pendingFaderAt;
    private long faderObservedAt, muteObservedAt;
    private string? oscAddressCache, muteOscAddressCache;
    public TimeProvider Clock { get; internal set; } = TimeProvider.System;

    protected AudioDevice(string name, int oscNumber, double targetLevelDb)
    {
        this.name = name;
        this.oscNumber = oscNumber;
        TargetLevelDb = targetLevelDb;
    }

    public string Name { get => name; set => name = value; }
    public int OscNumber => oscNumber;
    public double TargetLevelDb
    {
        get => targetLevelDb;
        set
        {
            if (!double.IsFinite(value) || value < -90 || value > 10) throw new ArgumentOutOfRangeException(nameof(value));
            targetLevelDb = value;
        }
    }
    public bool AutoLevelEnabled { get; set; } = true;
    public bool ProtectionEnabled { get; set; } = true;
    private double maxFaderDb = 10, maxGainReductionDb = 24;
    public double MaxFaderDb
    {
        get => maxFaderDb;
        set
        {
            if (!double.IsFinite(value) || value < -90 || value > 10) throw new ArgumentOutOfRangeException(nameof(value));
            maxFaderDb = value;
        }
    }
    public double MaxGainReductionDb
    {
        get => maxGainReductionDb;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 72) throw new ArgumentOutOfRangeException(nameof(value));
            maxGainReductionDb = value;
        }
    }
    public float LastSentFader
    {
        get => lastSentFader;
        set { if (float.IsFinite(value)) lastSentFader = Math.Clamp(value, 0, 1); }
    }
    public float CurrentFader
    {
        get => currentFader;
        set { if (float.IsFinite(value)) currentFader = Math.Clamp(value, 0, 1); }
    }
    public bool FaderInitialized => faderInitialized && Clock.GetElapsedTime(faderObservedAt).TotalMilliseconds < 6000;
    public bool MuteInitialized => muteInitialized && Clock.GetElapsedTime(muteObservedAt).TotalMilliseconds < 6000;
    public bool IsMuted { get => isMuted; set => isMuted = value; }
    public bool HasFreshMeters => GetLatestSample() is { } sample && sample.AgeMs() < METER_MAX_AGE_MS;
    public bool ReadyForLeveling => FaderInitialized && MuteInitialized && !IsMuted && HasFreshMeters;

    public abstract void ApplyCorrection(double deltaDb);
    protected abstract string BuildOscAddress();
    protected abstract string BuildMuteOscAddress();
    public string GetOscAddress() => oscAddressCache ??= BuildOscAddress();
    public string GetMuteOscAddress() => muteOscAddressCache ??= BuildMuteOscAddress();

    public void AddSample(MeterSample sample)
    {
        if (!float.IsFinite(sample.PostFaderLinear) || sample.PostFaderLinear < 0) return;
        lock (historyLock)
        {
            latest = sample;
            history.Enqueue((++sequence, sample));
            while (history.Count > MAX_HISTORY_SAMPLES || (history.Count > 0 && history.Peek().Sample.AgeMs() > HISTORY_RETENTION_MS))
                history.Dequeue();
        }
    }

    public MeterSample? GetLatestSample()
    {
        lock (historyLock) return latest;
    }

    public List<MeterSample> SnapshotHistory()
    {
        lock (historyLock) return history.Select(x => x.Sample).ToList();
    }

    public List<MeterSample> ReadSamplesSince(ref long cursor)
    {
        lock (historyLock)
        {
            var result = new List<MeterSample>();
            foreach (var item in history) if (item.Sequence > cursor) result.Add(item.Sample);
            cursor = sequence;
            return result;
        }
    }

    public double GetAverageDb(int windowMs)
    {
        lock (historyLock)
        {
            double sum = 0;
            int count = 0;
            long now = Clock.GetTimestamp();
            foreach (var item in history)
            {
                if (item.Sample.AgeMs(now) > windowMs) continue;
                double level = item.Sample.PostFaderLinear;
                sum += level * level;
                count++;
            }
            return count == 0 || sum <= 0 ? -90 : Math.Max(-90, 10 * Math.Log10(sum / count));
        }
    }

    public void UpdateFaderFromConsole(float fader)
    {
        if (!float.IsFinite(fader) || fader < 0 || fader > 1) return;
        lock (stateLock)
        {
            long now = Clock.GetTimestamp();
            while (sentFaders.Count > 0 && Clock.GetElapsedTime(sentFaders.Peek().Tick, now).TotalMilliseconds > ECHO_WINDOW_MS)
                sentFaders.Dequeue();
            currentFader = fader;
            if (!FaderInitialized)
            {
                lastSentFader = fader;
                faderInitialized = true;
                manualChange = false;
                sentFaders.Clear(); pendingFaderAt = null;
            }
            else
            {
                bool matchesLatest = Math.Abs(fader - lastSentFader) <= FADER_EPSILON;
                bool echo = matchesLatest || sentFaders.Any(x => Math.Abs(x.Value - fader) <= FADER_EPSILON);
                if (!echo) { manualChange = true; pendingFaderAt = null; }
                if (matchesLatest) pendingFaderAt = null;
            }
            faderObservedAt = now;
        }
    }

    public void RecordFaderSent(float value)
    {
        lock (stateLock)
        {
            long now = Clock.GetTimestamp();
            sentFaders.Enqueue((currentFader, now));
            sentFaders.Enqueue((value, now));
            while (sentFaders.Count > 256) sentFaders.Dequeue();
            lastSentFader = value;
            pendingFaderAt ??= now;
        }
    }

    public void ExpireUnconfirmedFader()
    {
        lock (stateLock)
        {
            if (pendingFaderAt is long pending && Clock.GetElapsedTime(pending).TotalMilliseconds > ECHO_WINDOW_MS)
            {
                faderInitialized = false;
                sentFaders.Clear();
                pendingFaderAt = null;
            }
        }
    }

    public void AcknowledgeManualChange()
    {
        lock (stateLock)
        {
            lastSentFader = currentFader;
            manualChange = false;
            sentFaders.Clear();
            pendingFaderAt = null;
        }
    }

    public void UpdateMuteFromConsole(int on)
    {
        if (on is not (0 or 1)) return;
        isMuted = on == 0;
        muteInitialized = true;
        muteObservedAt = Clock.GetTimestamp();
    }

    public virtual void ResetObservedState()
    {
        lock (stateLock)
        {
            faderInitialized = muteInitialized = false;
            currentFader = lastSentFader = 0;
            manualChange = false;
            pendingFaderAt = null;
            sentFaders.Clear();
        }
        lock (historyLock) { history.Clear(); latest = null; }
    }

    public virtual bool DetectManualChange() { lock (stateLock) return manualChange; }
    public override string ToString() => $"{GetType().Name} #{oscNumber} \"{name}\" target={targetLevelDb:F1}dB muted={isMuted}";
}
