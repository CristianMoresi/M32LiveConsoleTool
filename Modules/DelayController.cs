using System.Collections.Concurrent;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Modules;

/// <summary>Writes delay times only to a recently confirmed delay effect.</summary>
public class DelayController
{
    public const int MIN_SLOT_NUMBER = 1, MAX_SLOT_NUMBER = 8, MAX_DELAY_SLOT_NUMBER = 4;
    public const int TEMPO_MAX_AGE_MS = 4000;
    private readonly OscClient oscClient;
    private readonly ShowLogger logger;
    private readonly ConcurrentDictionary<int, DelaySlotConfig> delaySlots = new();
    private readonly ConcurrentDictionary<int, DetectedEffect> detectedEffects = new();
    private readonly Dictionary<int, long> detectedAt = new();
    private readonly Dictionary<int, (string Address, float Value, long At, bool Confirmed)> sent = new();
    private readonly Dictionary<int, double> requestedTimes = new();
    private double latestBpm;
    private long bpmAt;
    private volatile bool isRunning;
    private bool controlEnabled = true;
    private Thread? retryThread;
    private readonly ManualResetEventSlim stop = new(false);

    public DelayController(OscClient oscClient, ShowLogger logger)
    {
        this.oscClient = oscClient;
        this.logger = logger;
        oscClient.SessionInvalidated += ResetObservedState;
    }

    public IReadOnlyDictionary<int, DelaySlotConfig> DelaySlots => delaySlots;
    public IReadOnlyDictionary<int, DetectedEffect> DetectedEffects => detectedEffects;
    public bool IsControlEnabled { get { lock (oscClient.ControlLock) return controlEnabled; } }

    public void SuspendControl()
    {
        lock (oscClient.ControlLock) { controlEnabled = false; latestBpm = 0; requestedTimes.Clear(); }
    }

    public void ResumeControl()
    {
        lock (oscClient.ControlLock) controlEnabled = true;
    }

    public void Start()
    {
        if (isRunning) return;
        isRunning = true;
        stop.Reset();
        oscClient.MessageReceived += OnMessageReceived;
        RefreshEffects();
        retryThread = new Thread(() =>
        {
            while (!stop.Wait(1000))
                lock (oscClient.ControlLock)
                    for (int slot = 1; slot <= 8; slot++) QueryFxType(slot);
        })
        { IsBackground = true, Name = "FX sync" };
        retryThread.Start();
    }

    public void Stop()
    {
        isRunning = false;
        stop.Set();
        oscClient.MessageReceived -= OnMessageReceived;
        retryThread?.Join(1000);
    }

    private void ResetObservedState()
    {
        lock (oscClient.ControlLock)
        {
            detectedEffects.Clear(); detectedAt.Clear(); sent.Clear(); requestedTimes.Clear();
            latestBpm = 0;
        }
    }

    public void ClearTempo()
    {
        lock (oscClient.ControlLock) latestBpm = 0;
    }

    public void RefreshEffects()
    {
        lock (oscClient.ControlLock)
        {
            detectedEffects.Clear(); detectedAt.Clear(); sent.Clear();
            for (int slot = 1; slot <= 8; slot++) QueryFxType(slot);
        }
    }

    private void QueryFxType(int slot) => oscClient.Send(new OscMessage($"/fx/{slot}/type", ",", new()));

    internal void OnMessageReceived(OscMessage message)
    {
        lock (oscClient.ControlLock)
        {
            if (!isRunning) return;
            var parts = message.Address.Split('/');
            if (parts.Length < 4 || parts[1] != "fx" || !int.TryParse(parts[2], out int slot) || slot < 1 || slot > 8) return;
            if (parts.Length == 5 && parts[3] == "par" && message.Arguments.FirstOrDefault() is float actual
                && float.IsFinite(actual) && sent.TryGetValue(slot, out var command) && command.Address == message.Address)
            {
                if (Math.Abs(actual - command.Value) <= 0.0001f) sent[slot] = command with { Confirmed = true };
                return;
            }
            if (parts.Length != 4 || parts[3] != "type" || message.Arguments.FirstOrDefault() is not int type) return;
            var effect = FxEffectCatalog.Resolve(slot, type);
            if (effect == null)
            {
                detectedEffects.TryRemove(slot, out _); detectedAt.Remove(slot); sent.Remove(slot);
                return;
            }
            bool changed = !detectedEffects.TryGetValue(slot, out var previous) || previous.Name != effect.Name;
            if (changed)
            {
                sent.Remove(slot);
                logger.LogEvent($"DelayController|slot{slot}|detected|{effect.Name}", Severity.Info);
            }
            detectedEffects[slot] = effect;
            detectedAt[slot] = oscClient.Clock.GetTimestamp();
            ApplyConfiguredSlot(slot);
        }
    }

    public void ConfigureSlot(int slot, DelaySlotConfig config)
    {
        ValidateSlotNumber(slot);
        if (slot > MAX_DELAY_SLOT_NUMBER) throw new ArgumentOutOfRangeException(nameof(slot), "Only FX 1-4 support delays.");
        lock (oscClient.ControlLock)
        {
            delaySlots[slot] = config;
            requestedTimes.Remove(slot);
            ApplyConfiguredSlot(slot);
        }
    }

    public void ClearSlot(int slot)
    {
        ValidateSlotNumber(slot);
        lock (oscClient.ControlLock) { delaySlots.TryRemove(slot, out _); sent.Remove(slot); requestedTimes.Remove(slot); }
    }

    public void ApplyDelay(int slot, double timeMs, DelayEffectType fallbackType)
    {
        ValidateSlotNumber(slot);
        if (!double.IsFinite(timeMs) || timeMs <= 0) return;
        lock (oscClient.ControlLock)
        {
            requestedTimes[slot] = timeMs;
            SendDelayMessage(slot, timeMs);
        }
    }

    public void ApplyAllFromBpm(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm <= 0) return;
        lock (oscClient.ControlLock)
        {
            latestBpm = bpm;
            bpmAt = oscClient.Clock.GetTimestamp();
            foreach (int slot in delaySlots.Keys) ApplyConfiguredSlot(slot);
        }
    }

    public void ApplyAllCurrentSettings(double lastKnownBpm)
    {
        lock (oscClient.ControlLock)
        {
            if (double.IsFinite(lastKnownBpm) && lastKnownBpm > 0) { latestBpm = lastKnownBpm; bpmAt = oscClient.Clock.GetTimestamp(); }
            foreach (int slot in delaySlots.Keys) ApplyConfiguredSlot(slot);
        }
    }

    private void ApplyConfiguredSlot(int slot)
    {
        if (delaySlots.TryGetValue(slot, out var config))
        {
            if (!config.IsAutomatic) SendDelayMessage(slot, config.ManualMs);
            else if (latestBpm > 0 && oscClient.Clock.GetElapsedTime(bpmAt).TotalMilliseconds < TEMPO_MAX_AGE_MS)
                SendDelayMessage(slot, config.GetDelayMs(latestBpm));
        }
        else if (requestedTimes.TryGetValue(slot, out double time)) SendDelayMessage(slot, time);
    }

    private static void ValidateSlotNumber(int slot)
    {
        if (slot < 1 || slot > 8) throw new ArgumentOutOfRangeException(nameof(slot));
    }

    private void SendDelayMessage(int slot, double timeMs)
    {
        if (!controlEnabled || oscClient.WritesInhibited || slot > 4 || !double.IsFinite(timeMs) || timeMs <= 0
            || !detectedEffects.TryGetValue(slot, out var effect) || !effect.IsDelay
            || !detectedAt.TryGetValue(slot, out long at) || oscClient.Clock.GetElapsedTime(at).TotalMilliseconds >= 3000) return;
        string address = $"/fx/{slot}/par/{DelayEffects.TimeParameterNumber(effect.DelayType!.Value):D2}";
        float value = FaderMath.Normalize(timeMs, DelayEffects.TIME_MIN_MS, DelayEffects.TIME_MAX_MS);
        if (sent.TryGetValue(slot, out var previous) && previous.Address == address && previous.Value == value
            && (previous.Confirmed || oscClient.Clock.GetElapsedTime(previous.At).TotalMilliseconds < 1000)) return;
        if (!oscClient.SendControl(new OscMessage(address, ",f", new() { value }))) return;
        sent[slot] = (address, value, oscClient.Clock.GetTimestamp(), false);
        oscClient.Send(new OscMessage(address, ",", new()));
    }
}
