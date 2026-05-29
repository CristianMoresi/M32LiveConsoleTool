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

    /// <summary>Sleep granularity inside the process loop.</summary>
    private const int PROCESS_SLEEP_TICK_MS = 50;

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
    private ConcurrentDictionary<AudioDevice, DateTime?> manualChangeStateByDevice;

    /// <summary>UTC time of the last correction applied per device, for the dashboard state color.</summary>
    private ConcurrentDictionary<AudioDevice, DateTime> lastCorrectionAtByDevice;

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
        if (thresholdDb <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdDb),
                "The action threshold must be positive.");
        }
        if (slewUpDbPerSec <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slewUpDbPerSec),
                "The upward slew rate must be positive.");
        }
        if (slewDownDbPerSec <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slewDownDbPerSec),
                "The downward slew rate must be positive.");
        }
        if (dropLimitDb <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dropLimitDb),
                "The drop limit must be positive.");
        }
        if (cycleIntervalMs < 10 || cycleIntervalMs > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cycleIntervalMs),
                "The cycle interval must be between 10 and 5000 ms.");
        }

        this.oscClient = oscClient;
        this.meterReader = meterReader;
        this.logger = logger;
        this.devices = new List<AudioDevice>(devices);
        this.thresholdDb = thresholdDb;
        this.slewUpDbPerSec = slewUpDbPerSec;
        this.slewDownDbPerSec = slewDownDbPerSec;
        this.gateThresholdDb = gateThresholdDb;
        this.dropLimitDb = dropLimitDb;
        this.cycleIntervalMs = cycleIntervalMs;
        this.isEnabled = false;
        this.processThread = null;
        this.manualChangeStateByDevice = new ConcurrentDictionary<AudioDevice, DateTime?>();
        this.lastCorrectionAtByDevice = new ConcurrentDictionary<AudioDevice, DateTime>();
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

    public double ThresholdDb
    {
        get { return thresholdDb; }
        set { thresholdDb = value; }
    }

    /// <summary>Maximum upward correction rate (dB/s), used when raising a low level.</summary>
    public double SlewUpDbPerSec
    {
        get { return slewUpDbPerSec; }
        set { slewUpDbPerSec = value; }
    }

    /// <summary>Maximum downward correction rate (dB/s), used when lowering a high level.</summary>
    public double SlewDownDbPerSec
    {
        get { return slewDownDbPerSec; }
        set { slewDownDbPerSec = value; }
    }

    public double GateThresholdDb
    {
        get { return gateThresholdDb; }
        set { gateThresholdDb = value; }
    }

    public double DropLimitDb
    {
        get { return dropLimitDb; }
        set { dropLimitDb = value; }
    }

    public int CycleIntervalMs
    {
        get { return cycleIntervalMs; }
        set { cycleIntervalMs = value; }
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
        if (manualChangeStateByDevice.TryGetValue(device, out DateTime? manualAt) && manualAt != null)
        {
            return LevelingState.ManualOverride;
        }
        if (lastCorrectionAtByDevice.TryGetValue(device, out DateTime last)
            && (DateTime.UtcNow - last).TotalMilliseconds < CORRECTION_HIGHLIGHT_MS)
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
            if (average < gateThresholdDb)
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
            try
            {
                // Atomic snapshot: if the list changes mid-iteration, this cycle
                // finishes on the old version and the next one sees the new list.
                List<AudioDevice> snapshot = devices;

                foreach (AudioDevice device in snapshot)
                {
                    ProcessDevice(device);
                }
            }
            catch (Exception ex)
            {
                // A correction must never kill the module mid-show: log (throttled)
                // and keep running.
                LogProcessErrorThrottled(ex);
            }

            int slept = 0;
            while (slept < cycleIntervalMs && isEnabled)
            {
                Thread.Sleep(PROCESS_SLEEP_TICK_MS);
                slept += PROCESS_SLEEP_TICK_MS;
            }
        }
    }

    private void ProcessDevice(AudioDevice device)
    {
        // Guard 0: do nothing until the console has reported this device's real
        // fader position. Acting from the placeholder 0.0 (which maps to -inf dB)
        // would be meaningless and could never converge.
        if (!device.FaderInitialized)
        {
            return;
        }

        // Guard 1: mute.
        if (device.IsMuted)
        {
            return;
        }

        // Guard 2: stabilization period after a manual change.
        if (HandleManualChangeStabilization(device))
        {
            return;
        }

        // Guard 3: detect a new manual change.
        if (device.DetectManualChange())
        {
            manualChangeStateByDevice[device] = DateTime.UtcNow;
            faderAtStabilizationCheck[device] = device.CurrentFader;
            return;
        }

        // Read the moving average.
        double avgDb = meterReader.GetMovingAverage(device);

        // Guard 4: silence gate.
        if (avgDb < gateThresholdDb)
        {
            return;
        }

        double diffDb = device.TargetLevelDb - avgDb;

        // Guard 5: drop limit (intentional cut).
        if (diffDb > dropLimitDb)
        {
            return;
        }

        // Guard 6: action threshold.
        if (Math.Abs(diffDb) < thresholdDb)
        {
            return;
        }

        // Compute the slew-rate-limited correction, using the upward rate when
        // raising a low level and the downward rate when lowering a high one.
        double slewDbPerSec = diffDb >= 0 ? slewUpDbPerSec : slewDownDbPerSec;
        double maxStepDb = slewDbPerSec * (cycleIntervalMs / 1000.0);
        double correctionDb = CalculateCorrection(diffDb, maxStepDb);

        float faderBefore = device.LastSentFader;
        device.ApplyCorrection(correctionDb);

        // If the fader is pinned at a rail (for example already at the +10 dB
        // maximum) the correction produces no actual movement; skip the redundant
        // OSC write instead of resending the same value every cycle.
        if (Math.Abs(device.LastSentFader - faderBefore) < AudioDevice.FADER_EPSILON)
        {
            return;
        }

        // Optimistically record our own write as the current position so the next
        // cycle does not mistake it for a manual move before the console echoes
        // the value back. A genuine manual move will overwrite this via the
        // console's /xremote feedback.
        device.CurrentFader = device.LastSentFader;

        SendFaderUpdate(device);
        lastCorrectionAtByDevice[device] = DateTime.UtcNow;

        // Note: individual corrections are NOT logged, to avoid flooding the log
        // with hundreds of entries per minute. Only management events are logged
        // (enable, disable, recalibration, etc.).
    }

    private bool HandleManualChangeStabilization(AudioDevice device)
    {
        if (!manualChangeStateByDevice.TryGetValue(device, out DateTime? manualChangeAt))
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
            manualChangeStateByDevice[device] = DateTime.UtcNow;
            faderAtStabilizationCheck[device] = current;
            return true;
        }
        faderAtStabilizationCheck[device] = current;

        double elapsedMs = (DateTime.UtcNow - manualChangeAt.Value).TotalMilliseconds;

        if (elapsedMs < STABILIZATION_AFTER_MANUAL_MS)
        {
            return true;
        }

        // The wait is over: re-capture the target and clear the state.
        double newTargetDb = meterReader.GetMovingAverage(device);
        device.TargetLevelDb = newTargetDb;
        device.LastSentFader = device.CurrentFader;
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

    private void SendFaderUpdate(AudioDevice device)
    {
        OscMessage update = new OscMessage(
            device.GetOscAddress(),
            ",f",
            new List<object> { device.LastSentFader });

        oscClient.Send(update);
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
