using System.Collections.Concurrent;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Modules;

/// <summary>
/// Per-device leveling state, used by the dashboard to color each row: idle, under
/// the engineer's manual override, or being actively corrected by the leveler.
/// </summary>
public enum LevelingState
{
    Idle,
    ManualOverride,
    Correcting
}

/// <summary>
/// Automatic leveling module. Compensates slow level drift relative to the
/// target, inaudibly and while respecting the engineer's intent.
/// </summary>
/// <remarks>
/// <para>
/// Applies a strict ordered set of guards before acting:
/// </para>
/// <list type="number">
///   <item>Fader position known (skip until the console reports it).</item>
///   <item>Mute respected.</item>
///   <item>Stabilization period after a manual change.</item>
///   <item>New manual-change detection.</item>
///   <item>Silence gate.</item>
///   <item>Drop limit (intentional level cuts).</item>
///   <item>Action threshold.</item>
/// </list>
/// <para>
/// The resulting correction is clamped by the slew rate before being applied, and
/// the write to the console is sent over OSC.
/// </para>
/// <para>
/// <strong>Runtime modification</strong>: the device list can be changed during
/// the show via <see cref="AddDevice"/> and <see cref="RemoveDevice"/>. The list
/// uses a copy-on-write pattern (a <c>volatile</c> field replaced by a new list on
/// every modification) so the process thread always reads a coherent snapshot
/// without locks on the critical path.
/// </para>
/// <para>
/// <strong>Logging policy</strong>: the AutoLeveler makes hundreds of corrections
/// per minute. Logging them all would make a 3-hour show's log unmanageable and
/// flood the dashboard. Only management events are logged (start, stop, target
/// capture, device add/remove, target update after a manual move), not the
/// individual corrections.
/// </para>
/// </remarks>
public class AutoLeveler
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wait time (ms) after detecting a manual change before re-capturing the
    /// target level.
    /// </summary>
    private const int STABILIZATION_AFTER_MANUAL_MS = 4000;

    /// <summary>Cancelable wait for the next control deadline.</summary>
    private readonly ManualResetEventSlim stopSignal = new(false);
    private readonly Dictionary<AudioDevice, double> pendingCorrection = new();
    private readonly Dictionary<AudioDevice, long> previousCycle = new();
    private long generation = -1;

    /// <summary>Default duration of the initial target capture (sound check).</summary>
    public const int DEFAULT_TARGET_CAPTURE_MS = 2000;

    /// <summary>Window (ms) during which a device counts as "being corrected" for the UI.</summary>
    private const int CORRECTION_HIGHLIGHT_MS = 1500;

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private OscClient oscClient;
    private MeterReader meterReader;
    private ShowLogger logger;

    /// <summary>
    /// Watched devices, copy-on-write. Marked <c>volatile</c> so the assignments
    /// are visible across threads without a lock.
    /// </summary>
    private volatile List<AudioDevice> devices;

    private double thresholdDb;
    private double slewUpDbPerSec;
    private double slewDownDbPerSec;
    private double gateThresholdDb;
    private double dropLimitDb;
    private int cycleIntervalMs;

    private volatile bool isEnabled;
    private Thread? processThread;

    /// <summary>Tick of the last logged process-loop error, to throttle error logging.</summary>
    private long lastProcessErrorTick;

    /// <summary>
    /// Per-device manual-change state. A <c>ConcurrentDictionary</c> lets entries
    /// be added/removed while the process thread reads, with no external locks.
    /// </summary>
    private ConcurrentDictionary<AudioDevice, long?> manualChangeStateByDevice;

    /// <summary>Monotonic timestamp of the last correction applied per device, for the dashboard state color.</summary>
    private ConcurrentDictionary<AudioDevice, long> lastCorrectionAtByDevice;

    /// <summary>Fader position seen at the previous stabilization check, to detect an ongoing manual gesture.</summary>
    private ConcurrentDictionary<AudioDevice, float> faderAtStabilizationCheck;

    /// <summary>Lock serializing modifications to the device list.</summary>
    private readonly object devicesLock = new object();

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an AutoLeveler with all of its operating parameters validated.
    /// </summary>
    public AutoLeveler(
        OscClient oscClient,
        MeterReader meterReader,
        ShowLogger logger,
        List<AudioDevice> devices,
        double thresholdDb,
        double slewUpDbPerSec,
        double slewDownDbPerSec,
        double gateThresholdDb,
        double dropLimitDb,
        int cycleIntervalMs)
    {
        ValidateThreshold(thresholdDb);
        ValidateSlew(slewUpDbPerSec, "upward");
        ValidateSlew(slewDownDbPerSec, "downward");
        ValidateDropLimit(dropLimitDb);
        ValidateCycleInterval(cycleIntervalMs);

        this.oscClient = oscClient;
        this.meterReader = meterReader;
        this.logger = logger;
        this.devices = new List<AudioDevice>(devices);
        this.thresholdDb = thresholdDb;
        this.slewUpDbPerSec = slewUpDbPerSec;
        this.slewDownDbPerSec = slewDownDbPerSec;
        GateThresholdDb = gateThresholdDb;
        this.dropLimitDb = dropLimitDb;
        this.cycleIntervalMs = cycleIntervalMs;
        this.isEnabled = false;
        this.processThread = null;
        this.manualChangeStateByDevice = new ConcurrentDictionary<AudioDevice, long?>();
        this.lastCorrectionAtByDevice = new ConcurrentDictionary<AudioDevice, long>();
        this.faderAtStabilizationCheck = new ConcurrentDictionary<AudioDevice, float>();

        foreach (AudioDevice device in devices)
        {
            this.manualChangeStateByDevice[device] = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only view of the current device list. To modify it, use
    /// <see cref="AddDevice"/> or <see cref="RemoveDevice"/>.
    /// </summary>
    public IReadOnlyList<AudioDevice> Devices
    {
        get { return devices.AsReadOnly(); }
    }

    /// <remarks>
    /// The setters apply the same validation as the constructor, so a live edit
    /// can never put the module in a state the constructor would have rejected.
    /// </remarks>
    public double ThresholdDb
    {
        get { return thresholdDb; }
        set { ValidateThreshold(value); thresholdDb = value; }
    }

    /// <summary>Maximum upward correction rate (dB/s), used when raising a low level.</summary>
    public double SlewUpDbPerSec
    {
        get { return slewUpDbPerSec; }
        set { ValidateSlew(value, "upward"); slewUpDbPerSec = value; }
    }

    /// <summary>Maximum downward correction rate (dB/s), used when lowering a high level.</summary>
    public double SlewDownDbPerSec
    {
        get { return slewDownDbPerSec; }
        set { ValidateSlew(value, "downward"); slewDownDbPerSec = value; }
    }

    public double GateThresholdDb
    {
        get { return gateThresholdDb; }
        set { if (!double.IsFinite(value) || value < -90 || value > 0) throw new ArgumentOutOfRangeException(nameof(value)); gateThresholdDb = value; }
    }

    public double DropLimitDb
    {
        get { return dropLimitDb; }
        set { ValidateDropLimit(value); dropLimitDb = value; }
    }

    public int CycleIntervalMs
    {
        get { return cycleIntervalMs; }
        set { ValidateCycleInterval(value); cycleIntervalMs = value; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Parameter validation (shared by the constructor and the setters)
    // ─────────────────────────────────────────────────────────────────────

    private static void ValidateThreshold(double thresholdDb)
    {
        if (!double.IsFinite(thresholdDb) || thresholdDb <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdDb),
                "The action threshold must be positive.");
        }
    }

    private static void ValidateSlew(double slewDbPerSec, string direction)
    {
        if (!double.IsFinite(slewDbPerSec) || slewDbPerSec <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slewDbPerSec),
                $"The {direction} slew rate must be positive.");
        }
    }

    private static void ValidateDropLimit(double dropLimitDb)
    {
        if (!double.IsFinite(dropLimitDb) || dropLimitDb <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dropLimitDb),
                "The drop limit must be positive.");
        }
    }

    private static void ValidateCycleInterval(int cycleIntervalMs)
    {
        if (cycleIntervalMs < 10 || cycleIntervalMs > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cycleIntervalMs),
                "The cycle interval must be between 10 and 5000 ms.");
        }
    }

    public bool IsEnabled
    {
        get { return isEnabled; }
    }

    /// <summary>
    /// Current leveling state of a device, for the dashboard's per-row color: under
    /// the engineer's manual override, actively being corrected, or idle.
    /// </summary>
    public LevelingState GetDeviceState(AudioDevice device)
    {
        if (manualChangeStateByDevice.TryGetValue(device, out long? manualAt) && manualAt != null)
        {
            return LevelingState.ManualOverride;
        }
        if (lastCorrectionAtByDevice.TryGetValue(device, out long last)
            && oscClient.Clock.GetElapsedTime(last).TotalMilliseconds < CORRECTION_HIGHLIGHT_MS)
        {
            return LevelingState.Correcting;
        }
        return LevelingState.Idle;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Runtime modification of the device list
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a device to the watched list. Initializes its manual-change state to
    /// <c>null</c>.
    /// </summary>
    public void AddDevice(AudioDevice device)
    {
        lock (devicesLock)
        {
            if (devices.Contains(device))
            {
                return;
            }

            List<AudioDevice> updated = new List<AudioDevice>(devices);
            updated.Add(device);
            devices = updated;

            manualChangeStateByDevice[device] = null;
        }
    }

    /// <summary>
    /// Removes a device from the watched list and clears its internal state.
    /// </summary>
    public void RemoveDevice(AudioDevice device)
    {
        lock (devicesLock)
        {
            if (!devices.Contains(device))
            {
                return;
            }

            List<AudioDevice> updated = new List<AudioDevice>(devices);
            updated.Remove(device);
            devices = updated;

            manualChangeStateByDevice.TryRemove(device, out _);
            lastCorrectionAtByDevice.TryRemove(device, out _);
            faderAtStabilizationCheck.TryRemove(device, out _);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Target capture (sound check)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures each device's target level by averaging its meters over the given
    /// period. Blocks the calling thread.
    /// </summary>
    public void CaptureTargets(int durationMs)
    {
        Thread.Sleep(durationMs);

        List<AudioDevice> snapshot = devices;

        int captured = 0;
        int keptSilent = 0;

        foreach (AudioDevice device in snapshot)
        {
            double average = meterReader.GetMovingAverage(device);

            // Never capture a target from silence: it would set the target to the
            // -90 dB floor and make the leveler pull the fader all the way down when
            // the signal returns. Keep the previous target for a silent device.
            if (!device.ReadyForLeveling || !double.IsFinite(average) || average < gateThresholdDb)
            {
                keptSilent++;
                continue;
            }

            device.TargetLevelDb = average;
            captured++;
        }

        logger.LogEvent(
            $"AutoLeveler|targets recalibrated (captured {captured}, kept {keptSilent} silent)",
            Severity.Info);
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

        stopSignal.Reset();
        isEnabled = true;

        processThread = new Thread(ProcessLoop);
        processThread.IsBackground = true;
        processThread.Name = "AutoLeveler-Process";
        processThread.Start();

        logger.LogEvent("AutoLeveler enabled", Severity.Info);
    }

    public void Stop()
    {
        if (!isEnabled)
        {
            return;
        }

        isEnabled = false;
        stopSignal.Set();

        if (processThread != null && processThread.IsAlive)
        {
            processThread.Join(1000);
            processThread = null;
        }

        logger.LogEvent("AutoLeveler disabled", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Process loop
    // ─────────────────────────────────────────────────────────────────────

    private void ProcessLoop()
    {
        while (isEnabled)
        {
            long started = oscClient.Clock.GetTimestamp();
            try { foreach (var device in devices) ProcessDevice(device); }
            catch (Exception ex) { LogProcessErrorThrottled(ex); }
            double elapsed = oscClient.Clock.GetElapsedTime(started).TotalMilliseconds;
            if (stopSignal.Wait(Math.Max(1, cycleIntervalMs - (int)elapsed))) break;
        }
    }

    internal void ProcessDevice(AudioDevice device)
    {
        lock (oscClient.ControlLock)
        {
            if (generation != oscClient.Generation)
            {
                generation = oscClient.Generation;
                pendingCorrection.Clear(); previousCycle.Clear();
                foreach (var d in devices) manualChangeStateByDevice[d] = null;
                lastCorrectionAtByDevice.Clear(); faderAtStabilizationCheck.Clear();
            }
            long now = oscClient.Clock.GetTimestamp();
            double dt = previousCycle.TryGetValue(device, out long previous)
                ? Math.Clamp(oscClient.Clock.GetElapsedTime(previous, now).TotalSeconds, 0, Math.Min(0.25, cycleIntervalMs / 1000.0 * 2))
                : cycleIntervalMs / 1000.0;
            previousCycle[device] = now;
            device.ExpireUnconfirmedFader();
            if (oscClient.WritesInhibited || !device.AutoLevelEnabled || !device.ReadyForLeveling || !devices.Contains(device))
            {
                pendingCorrection.Remove(device);
                return;
            }
            if (HandleManualChangeStabilization(device)) { pendingCorrection.Remove(device); return; }
            if (device.DetectManualChange())
            {
                manualChangeStateByDevice[device] = now;
                faderAtStabilizationCheck[device] = device.CurrentFader;
                pendingCorrection.Remove(device);
                return;
            }
            double average = meterReader.GetMovingAverage(device);
            double startDb = FaderMath.FaderToDb(device.LastSentFader);
            double levelDifference = device.TargetLevelDb - average;
            double difference = Math.Min(levelDifference, device.MaxFaderDb - startDb);
            if (!double.IsFinite(average) || average < gateThresholdDb || difference > dropLimitDb
                || (Math.Abs(levelDifference) < thresholdDb && startDb <= device.MaxFaderDb))
            {
                pendingCorrection.Remove(device);
                return;
            }
            double step = CalculateCorrection(difference, (difference >= 0 ? slewUpDbPerSec : slewDownDbPerSec) * dt);
            double pending = pendingCorrection.GetValueOrDefault(device);
            if (Math.Sign(step) != Math.Sign(pending)) pending = 0;
            pending += step;
            // Accumulation must not pass the target or a ceiling closer than one send quantum.
            pending = Math.CopySign(Math.Min(Math.Abs(pending), Math.Abs(difference)), pending);
            float proposed = FaderMath.DbToFader(startDb + pending);
            if (Math.Abs(proposed - device.LastSentFader) < AudioDevice.FADER_EPSILON)
            {
                pendingCorrection[device] = Math.Clamp(pending, -1, 1);
                return;
            }
            if (oscClient.SendControl(new OscMessage(device.GetOscAddress(), ",f", new() { proposed })))
            {
                device.RecordFaderSent(proposed);
                pendingCorrection.Remove(device);
                lastCorrectionAtByDevice[device] = now;
            }
        }
    }

    private bool HandleManualChangeStabilization(AudioDevice device)
    {
        if (!manualChangeStateByDevice.TryGetValue(device, out long? manualChangeAt))
        {
            // The device was just removed: skip this cycle.
            return true;
        }

        if (manualChangeAt == null)
        {
            return false;
        }

        // If the engineer is still moving the fader, extend the window instead of
        // re-capturing mid-gesture: only start the countdown once the fader is still.
        float current = device.CurrentFader;
        if (faderAtStabilizationCheck.TryGetValue(device, out float previous)
            && Math.Abs(current - previous) > AudioDevice.FADER_EPSILON)
        {
            manualChangeStateByDevice[device] = oscClient.Clock.GetTimestamp();
            faderAtStabilizationCheck[device] = current;
            return true;
        }
        faderAtStabilizationCheck[device] = current;

        double elapsedMs = oscClient.Clock.GetElapsedTime(manualChangeAt.Value).TotalMilliseconds;

        if (elapsedMs < STABILIZATION_AFTER_MANUAL_MS)
        {
            return true;
        }

        // Never capture a target from silence (same guard as CaptureTargets): if
        // the engineer moved the fader during a pause between songs, the average
        // sits at the -90 dB floor, and capturing it would make the leveler pull
        // the channel all the way down once the music returns. Stay in the manual
        // stand-down (re-arming the window) until there is real signal to measure.
        double newTargetDb = meterReader.GetMovingAverage(device);
        if (newTargetDb < gateThresholdDb)
        {
            manualChangeStateByDevice[device] = oscClient.Clock.GetTimestamp();
            return true;
        }

        // The wait is over: re-capture the target and clear the state.
        device.TargetLevelDb = newTargetDb;
        device.AcknowledgeManualChange();
        manualChangeStateByDevice[device] = null;

        // Do not apply a correction this cycle: normal operation resumes next one.
        return true;
    }

    private double CalculateCorrection(double diffDb, double maxStepDb)
    {
        double absDiff = Math.Abs(diffDb);
        double absStep = Math.Min(absDiff, maxStepDb);
        return Math.Sign(diffDb) * absStep;
    }



    /// <summary>Logs a process-loop error at most once every few seconds.</summary>
    private void LogProcessErrorThrottled(Exception ex)
    {
        long now = Environment.TickCount64;
        if (now - lastProcessErrorTick > 5000)
        {
            lastProcessErrorTick = now;
            logger.LogEvent($"AutoLeveler|process error|{ex.Message}", Severity.Warning);
        }
    }
}
