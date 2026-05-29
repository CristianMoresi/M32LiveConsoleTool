using System.Text.Json;
using System.Text.Json.Serialization;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Ui;

/// <summary>
/// Central application orchestrator. Builds, starts, stops and persists the state
/// of every module. It is the single entry point for the menu's operations and
/// the only class that knows the full dependencies between modules.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Responsibilities</strong>:
/// </para>
/// <list type="bullet">
///   <item>Create every module in the right order and resolve their cross
///   dependencies.</item>
///   <item>Decide whether to start the <see cref="M32Simulator"/> and point the
///   <see cref="OscClient"/> at loopback or at the real IP.</item>
///   <item>Centralize operations that touch several modules at once (adding a
///   channel registers it in MeterReader, AutoLeveler and AntiClipper together).</item>
///   <item>Load and persist the <see cref="SessionConfig"/> on disk.</item>
///   <item>Coordinate clean shutdown in reverse startup order.</item>
/// </list>
/// <para>
/// <strong>Construction order</strong>: each module is built only AFTER the ones
/// it needs. Logger first, then OscClient, then MeterReader, then DelayController,
/// then the automatic modules. The order is dictated by the dependencies and
/// cannot be reversed.
/// </para>
/// <para>
/// <strong>Stop order</strong>: the reverse of startup. First the modules that
/// generate OSC traffic stop (BpmDetector, AntiClipper, AutoLeveler), then the
/// MeterReader, then the OscClient, finally the simulator if it was running. This
/// guarantees that when a socket is closed, nobody is still trying to write to it.
/// </para>
/// <para>
/// <strong>Shutdown idempotency</strong>: <see cref="Shutdown"/> can be called
/// several times (from the Q key, from Ctrl+C, from the process-exit handler). The
/// <c>isShutdown</c> flag under <c>shutdownLock</c> guarantees that only the first
/// call does anything; the rest return immediately.
/// </para>
/// </remarks>
public class AppOrchestrator
{
    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private SessionConfig config;

    private OscClient? oscClient;
    private ShowLogger? logger;
    private MeterReader? meterReader;
    private DelayController? delayController;
    private AutoLeveler? autoLeveler;
    private AntiClipper? antiClipper;
    private BpmDetector? bpmDetector;
    private M32Simulator? simulator;

    private bool simulationMode;
    private bool isShutdown;

    /// <summary>UTC instant the session started (modules came up), for the uptime display.</summary>
    private DateTime sessionStartUtc;

    /// <summary>Lock guaranteeing Shutdown runs only once even if called several times.</summary>
    private readonly object shutdownLock = new object();

    /// <summary>
    /// Lock shared with the dashboard and the menu to serialize all console
    /// writes. It lives here because this is the only place both components know.
    /// </summary>
    private readonly object consoleLock = new object();

    /// <summary>JSON options for writing session files (indented, enums as names).</summary>
    private static readonly JsonSerializerOptions JsonWriteOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>JSON options for reading session files (enums as names).</summary>
    private static readonly JsonSerializerOptions JsonReadOptions = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the orchestrator with an empty configuration. The real config is
    /// loaded later in <see cref="Initialize"/>, reading from disk or using the
    /// defaults.
    /// </summary>
    public AppOrchestrator()
    {
        config = new SessionConfig();
        isShutdown = false;
        simulationMode = false;
        sessionStartUtc = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties (module and state access)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Current session configuration. Modifiable from the menu.</summary>
    public SessionConfig Config { get { return config; } }

    /// <summary>OSC client. Not null after <see cref="Initialize"/>.</summary>
    public OscClient OscClient { get { return oscClient!; } }

    /// <summary>Show event logger.</summary>
    public ShowLogger Logger { get { return logger!; } }

    /// <summary>Meter reader.</summary>
    public MeterReader MeterReader { get { return meterReader!; } }

    /// <summary>Delay-slot controller.</summary>
    public DelayController DelayController { get { return delayController!; } }

    /// <summary>Automatic leveling module.</summary>
    public AutoLeveler AutoLeveler { get { return autoLeveler!; } }

    /// <summary>Automatic anti-clipping module.</summary>
    public AntiClipper AntiClipper { get { return antiClipper!; } }

    /// <summary>BPM detector.</summary>
    public BpmDetector BpmDetector { get { return bpmDetector!; } }

    /// <summary><c>true</c> if the session started in simulation mode (no physical console).</summary>
    public bool SimulationMode { get { return simulationMode; } }

    /// <summary>
    /// Time elapsed since <see cref="Start"/> brought the session up. Shown in the
    /// dashboard header so the engineer can see how long the tool has been running.
    /// </summary>
    public TimeSpan SessionUptime { get { return DateTime.UtcNow - sessionStartUtc; } }

    /// <summary>
    /// Lock shared with the dashboard and the menu to serialize console writes.
    /// </summary>
    public object ConsoleLock { get { return consoleLock; } }

    // ─────────────────────────────────────────────────────────────────────
    // Configuration persistence
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Indicates whether a previous session configuration file already exists.
    /// Used by <see cref="Program"/> to decide whether to run the first-run wizard
    /// before initializing.
    /// </summary>
    public static bool HasExistingConfig()
    {
        return File.Exists(AppPaths.LastSessionFile);
    }

    /// <summary>
    /// Serializes a session config to <paramref name="path"/> as indented JSON,
    /// creating the app data directories first. Static so the first-run flow can
    /// persist the initial config without constructing a full orchestrator.
    /// </summary>
    public static void WriteSession(SessionConfig config, string path)
    {
        AppPaths.EnsureDirectoriesExist();
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonWriteOptions));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Initialization and startup
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes every module. Loads the configuration from disk, starts the
    /// logger, decides the IP (loopback if simulation, real IP otherwise) and
    /// builds the modules in dependency order. After this call all modules exist
    /// but none is running.
    /// </summary>
    /// <param name="useSimulator"><c>true</c> to also start the local simulator.</param>
    public void Initialize(bool useSimulator)
    {
        simulationMode = useSimulator;

        // In simulation, always load a fresh demo session (ignoring any saved
        // last_session) so the dashboard comes up populated with example channels
        // every time. Real-console mode loads the saved session as usual.
        config = simulationMode
            ? SessionConfig.CreateSimulationDemo()
            : LoadConfigOrDefaults();

        logger = new ShowLogger();
        logger.Open(AppPaths.GetSessionLogPath());
        AppPaths.PruneOldSessionLogs();

        string mixerIp;
        if (simulationMode)
        {
            mixerIp = M32Simulator.LOOPBACK_IP;
            simulator = new M32Simulator(config.MixerSendPort, logger);
        }
        else
        {
            mixerIp = config.MixerIp;
        }

        oscClient = new OscClient(mixerIp, config.MixerSendPort, config.MixerReceivePort);

        List<AudioDevice> devices = BuildDevicesFromConfig();

        meterReader = new MeterReader(oscClient, devices, config.MeterWindowMs);

        delayController = new DelayController(oscClient, logger);
        ApplyDelaySlotsFromConfig();

        autoLeveler = new AutoLeveler(
            oscClient, meterReader, logger, devices,
            config.AutoLevelerThresholdDb,
            config.AutoLevelerSlewUpDbPerSec,
            config.AutoLevelerSlewDownDbPerSec,
            config.AutoLevelerGateThresholdDb,
            config.AutoLevelerDropLimitDb,
            config.AutoLevelerCycleIntervalMs);

        List<MixerChannel> channelsOnly = new List<MixerChannel>();
        foreach (AudioDevice device in devices)
        {
            if (device is MixerChannel channel)
            {
                channelsOnly.Add(channel);
            }
        }

        antiClipper = new AntiClipper(
            oscClient, meterReader, logger, channelsOnly,
            config.AntiClipperClipThresholdDb,
            config.AntiClipperReductionStepDb,
            config.AntiClipperMaxReductionStepDb,
            config.AntiClipperSevereOvershootDb,
            config.AntiClipperConfirmationSamples,
            config.AntiClipperCooldownMs);

        MixerChannel? bpmTarget = FindBpmTargetChannel(devices);
        MixerChannel effectiveTarget = bpmTarget ?? new MixerChannel("(unassigned)", 1, 0, 0);

        bpmDetector = new BpmDetector(
            meterReader, delayController, logger,
            effectiveTarget,
            config.BpmDetectorMode,
            config.BpmTransientThresholdDb,
            config.BpmChangeHysteresis);
    }

    /// <summary>
    /// Starts the simulator (if applicable), connects the OSC client, starts the
    /// meter reader and enables the automatic modules that were marked active in
    /// the configuration. Also applies the initial delay-slot settings to the
    /// console.
    /// </summary>
    public void Start()
    {
        sessionStartUtc = DateTime.UtcNow;

        if (simulationMode && simulator != null)
        {
            simulator.Start();
        }

        if (!oscClient!.Connect() && !simulationMode)
        {
            // Surface why no meters will arrive (e.g. the receive port is in use)
            // instead of leaving the engineer guessing at a silent NO SIGNAL.
            logger!.LogEvent(
                $"OscClient|connect failed on receive port {config.MixerReceivePort}: {oscClient.ConnectError}",
                Severity.Error);
        }
        oscClient.StartKeepAlive();
        meterReader!.StartReading();
        delayController!.Start();

        if (config.AutoLevelerEnabled)
        {
            autoLeveler!.Start();
        }
        if (config.AntiClipperEnabled)
        {
            antiClipper!.Monitor();
        }
        if (config.BpmDetectorEnabled)
        {
            bpmDetector!.Start();
        }

        delayController!.ApplyAllCurrentSettings(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Module toggles
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns the AutoLeveler on or off. Also updates the persistent flag in the
    /// configuration so the state is restored after a restart.
    /// </summary>
    public void ToggleAutoLeveler()
    {
        if (autoLeveler!.IsEnabled)
        {
            autoLeveler.Stop();
            config.AutoLevelerEnabled = false;
        }
        else
        {
            autoLeveler.Start();
            config.AutoLevelerEnabled = true;
        }
    }

    /// <summary>
    /// Turns the AntiClipper on or off. The module's start method is called
    /// <c>Monitor()</c>, not <c>Start()</c>, for semantic consistency with the
    /// module's nature (it watches, it does not produce).
    /// </summary>
    public void ToggleAntiClipper()
    {
        if (antiClipper!.IsEnabled)
        {
            antiClipper.Stop();
            config.AntiClipperEnabled = false;
        }
        else
        {
            antiClipper.Monitor();
            config.AntiClipperEnabled = true;
        }
    }

    /// <summary>
    /// Turns the BpmDetector on or off.
    /// </summary>
    public void ToggleBpmDetector()
    {
        if (bpmDetector!.IsEnabled)
        {
            bpmDetector.Stop();
            config.BpmDetectorEnabled = false;
        }
        else
        {
            bpmDetector.Start();
            config.BpmDetectorEnabled = true;
        }
    }

    /// <summary>
    /// Panic / release-all: immediately disables every automatic module so the tool
    /// stops touching the console and the engineer regains full manual control. The
    /// off state is persisted, and the event is logged as a Warning so it shows on
    /// the dashboard. Faders and preamps keep their current positions (nothing is
    /// reset); the modules simply stop acting.
    /// </summary>
    public void ReleaseAll()
    {
        if (autoLeveler!.IsEnabled)
        {
            autoLeveler.Stop();
        }
        if (antiClipper!.IsEnabled)
        {
            antiClipper.Stop();
        }
        if (bpmDetector!.IsEnabled)
        {
            bpmDetector.Stop();
        }

        config.AutoLevelerEnabled = false;
        config.AntiClipperEnabled = false;
        config.BpmDetectorEnabled = false;

        logger!.LogEvent("All automatic control released (panic) — manual control restored", Severity.Warning);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Device-list modification
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a channel to the watched list. Registers it in MeterReader (to
    /// subscribe to its meters), AutoLeveler (to level it) and AntiClipper (to
    /// protect it from clipping), and updates the persistent configuration.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the channel was added; <c>false</c> if a channel with the same
    /// OSC number was already watched (no duplicate is created).
    /// </returns>
    public bool AddChannel(MixerChannel channel)
    {
        if (DeviceExists("channel", channel.OscNumber))
        {
            return false;
        }

        meterReader!.AddDevice(channel);
        autoLeveler!.AddDevice(channel);
        antiClipper!.AddChannel(channel);

        config.WatchedDevices.Add(AudioDeviceDescriptor.FromAudioDevice(channel));

        // If this is the channel the BpmDetector was configured to follow but that
        // did not exist yet (so the detector is bound to a placeholder), bind it to
        // the real channel now.
        if (channel.OscNumber == config.BpmTargetChannelOscNumber)
        {
            bpmDetector!.TargetChannel = channel;
        }

        return true;
    }

    /// <summary>Removes a channel from the modules and the configuration.</summary>
    public void RemoveChannel(MixerChannel channel)
    {
        meterReader!.RemoveDevice(channel);
        autoLeveler!.RemoveDevice(channel);
        antiClipper!.RemoveChannel(channel);

        config.WatchedDevices.RemoveAll(d => d.Type == "channel" && d.OscNumber == channel.OscNumber);

        // If this was the BpmDetector's target, unbind it so detection does not keep
        // pointing at a channel that no longer streams meters.
        if (config.BpmTargetChannelOscNumber == channel.OscNumber)
        {
            bpmDetector!.TargetChannel = new MixerChannel("(unassigned)", 1, 0, 0);
            config.BpmTargetChannelOscNumber = 0;
        }
    }

    /// <summary>
    /// Adds a bus to the watched list. Registers it only in MeterReader and
    /// AutoLeveler: the AntiClipper does not act on buses.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the bus was added; <c>false</c> if a bus with the same OSC
    /// number was already watched (no duplicate is created).
    /// </returns>
    public bool AddBus(Bus bus)
    {
        if (DeviceExists("bus", bus.OscNumber))
        {
            return false;
        }

        meterReader!.AddDevice(bus);
        autoLeveler!.AddDevice(bus);

        config.WatchedDevices.Add(AudioDeviceDescriptor.FromAudioDevice(bus));
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> if a device of the given type (<c>"channel"</c> or
    /// <c>"bus"</c>) and OSC number is already watched, so callers can avoid creating
    /// a duplicate (the device lists key devices by reference, not by number).
    /// </summary>
    private bool DeviceExists(string type, int oscNumber)
    {
        foreach (AudioDevice d in meterReader!.Devices)
        {
            bool typeMatches = (type == "channel" && d is MixerChannel)
                            || (type == "bus" && d is Bus);
            if (typeMatches && d.OscNumber == oscNumber)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Removes a bus from the modules and the configuration.</summary>
    public void RemoveBus(Bus bus)
    {
        meterReader!.RemoveDevice(bus);
        autoLeveler!.RemoveDevice(bus);

        config.WatchedDevices.RemoveAll(d => d.Type == "bus" && d.OscNumber == bus.OscNumber);
    }

    /// <summary>
    /// Renames an existing device, keeping its position in the watched list and
    /// its full state (target, preamp, fader, mute, sample history). Since only
    /// the name changes, there is no need to touch OSC subscriptions or rebuild
    /// anything: the device's OSC number is what the console identifies, and that
    /// does not change.
    /// </summary>
    /// <returns><c>true</c> if it was found and renamed; <c>false</c> if it did not exist.</returns>
    public bool RenameDevice(string deviceType, int oscNumber, string newName)
    {
        AudioDevice? found = null;
        foreach (AudioDevice d in meterReader!.Devices)
        {
            bool typeMatches = (deviceType == "channel" && d is MixerChannel)
                            || (deviceType == "bus" && d is Bus);
            if (typeMatches && d.OscNumber == oscNumber)
            {
                found = d;
                break;
            }
        }

        if (found == null)
        {
            return false;
        }

        // Mutate the name in place. The device keeps its position in every
        // internal list (MeterReader, AutoLeveler, AntiClipper) and its sample
        // history intact.
        found.Name = newName;

        // Also update the descriptor in the persistent configuration so the new
        // name is saved when the session closes.
        foreach (AudioDeviceDescriptor desc in config.WatchedDevices)
        {
            bool descTypeMatches = (deviceType == "channel" && desc.Type == "channel")
                                || (deviceType == "bus" && desc.Type == "bus");
            if (descTypeMatches && desc.OscNumber == oscNumber)
            {
                desc.Name = newName;
                break;
            }
        }

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // BpmDetector operations
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Changes the BpmDetector's target channel. If the channel is not already in
    /// the watched list, it is first added to the MeterReader so the detector can
    /// receive samples from it.
    /// </summary>
    public void SetBpmTargetChannel(MixerChannel channel)
    {
        if (!meterReader!.Devices.Contains(channel))
        {
            meterReader.AddDevice(channel);
        }

        bpmDetector!.TargetChannel = channel;
        config.BpmTargetChannelOscNumber = channel.OscNumber;
    }

    /// <summary>
    /// Changes the BpmDetector's detection mode (Click, BeatTracking, ManualTap).
    /// </summary>
    public void SetBpmMode(BpmDetectionMode mode)
    {
        bpmDetector!.Mode = mode;
        config.BpmDetectorMode = mode;
    }

    /// <summary>
    /// Records a manual tap-tempo press. Called by the menu when the engineer
    /// presses the T key in the main menu.
    /// </summary>
    public void TapTempo()
    {
        bpmDetector!.TapTempo();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Delay-slot operations
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures a delay slot with the given mode and parameters. Updates the
    /// persistent configuration and immediately applies the delay time to the
    /// console: if automatic, it uses the current BPM if any; if manual, the fixed
    /// milliseconds.
    /// </summary>
    public void ConfigureDelaySlot(int slot, DelaySlotConfig slotConfig)
    {
        delayController!.ConfigureSlot(slot, slotConfig);

        config.DelaySlots.RemoveAll(d => d.Slot == slot);
        DelaySlotDescriptor descriptor = new DelaySlotDescriptor();
        descriptor.Slot = slot;
        descriptor.EffectType = slotConfig.EffectType;
        descriptor.IsAutomatic = slotConfig.IsAutomatic;
        descriptor.Beat = slotConfig.Beat;
        descriptor.ManualMs = slotConfig.ManualMs;
        config.DelaySlots.Add(descriptor);

        if (slotConfig.IsAutomatic)
        {
            if (bpmDetector!.HasEverBeenValid)
            {
                delayController.ApplyDelay(slot, slotConfig.GetDelayMs(bpmDetector.CurrentBpm), slotConfig.EffectType);
            }
        }
        else
        {
            delayController.ApplyDelay(slot, slotConfig.ManualMs, slotConfig.EffectType);
        }
    }

    /// <summary>
    /// Frees a delay slot. The slot is no longer managed by the app: the console
    /// keeps the last value it was sent, but BPM changes no longer affect it.
    /// </summary>
    public void ClearDelaySlot(int slot)
    {
        delayController!.ClearSlot(slot);
        config.DelaySlots.RemoveAll(d => d.Slot == slot);
    }

    /// <summary>
    /// Re-queries the console for the effect loaded in each FX slot. Use when the
    /// engineer changes effects during the show so delays map to the right time
    /// parameter.
    /// </summary>
    public void RefreshEffects()
    {
        delayController!.RefreshEffects();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Other operations
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recaptures the current levels as the AutoLeveler's new targets. Blocks for
    /// <see cref="AutoLeveler.DEFAULT_TARGET_CAPTURE_MS"/> to average the levels
    /// before fixing them.
    /// </summary>
    public void RecalibrateTargets()
    {
        autoLeveler!.CaptureTargets(AutoLeveler.DEFAULT_TARGET_CAPTURE_MS);
    }

    /// <summary>
    /// Changes the mixer IP on the fly. Subsequent OSC commands go to the new IP.
    /// If the new IP does not respond, meters stop arriving but the program does
    /// not crash. In simulation this has no effect (the simulated console lives on
    /// loopback).
    /// </summary>
    public void SetMixerIp(string newIp)
    {
        config.MixerIp = newIp;

        if (simulationMode || oscClient == null)
        {
            return;
        }

        oscClient.MixerIp = newIp;

        // The new console starts with an empty subscription table (/renew only
        // refreshes existing entries), so re-send every subscription and re-detect
        // the FX. Otherwise no meters or effects would arrive from the new IP until
        // a restart. The keep-alive thread re-asserts /xremote on its own.
        meterReader?.ResubscribeAll();
        delayController?.RefreshEffects();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Session reset
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears the current session configuration while keeping the network data
    /// (IP, ports) and the file locations. Turns off the automatic modules,
    /// removes every watched device and frees every delay slot. From here the
    /// engineer can set up a fresh session from scratch using the normal menu
    /// flows, without deleting channels one by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept: <c>MixerIp</c>, <c>MixerSendPort</c>, <c>MixerReceivePort</c>, the
    /// modules' internal parameters (thresholds, slew rate, cooldown, etc.) and the
    /// files on disk.
    /// </para>
    /// <para>
    /// Cleared: the list of watched channels and buses, configured delay slots,
    /// the BpmDetector's target channel, and the on/off state of the three
    /// automatic modules.
    /// </para>
    /// <para>
    /// The reset does NOT overwrite the configuration file on disk immediately: the
    /// empty session is only persisted on the next clean shutdown (or when the
    /// engineer saves a preset). If the program is killed before closing, the file
    /// keeps the previous session.
    /// </para>
    /// </remarks>
    public void ResetSession()
    {
        // Turn off the automatic modules.
        if (autoLeveler!.IsEnabled)
        {
            autoLeveler.Stop();
        }
        if (antiClipper!.IsEnabled)
        {
            antiClipper.Stop();
        }
        if (bpmDetector!.IsEnabled)
        {
            bpmDetector.Stop();
        }

        // Clear the delay slots.
        foreach (int slot in delayController!.DelaySlots.Keys.ToList())
        {
            delayController.ClearSlot(slot);
        }
        config.DelaySlots.Clear();

        // Clear the watched devices. Iterate over a copy because
        // RemoveChannel/RemoveBus modify the MeterReader's list.
        List<AudioDevice> devicesCopy = new List<AudioDevice>(meterReader!.Devices);
        foreach (AudioDevice device in devicesCopy)
        {
            if (device is MixerChannel channel)
            {
                RemoveChannel(channel);
            }
            else if (device is Bus bus)
            {
                RemoveBus(bus);
            }
        }
        config.WatchedDevices.Clear();

        // Reset the BpmDetector's reference to an empty channel.
        bpmDetector.TargetChannel = new MixerChannel("(unassigned)", 1, 0, 0);
        config.BpmTargetChannelOscNumber = 0;

        // Module flags off in the persistent configuration.
        config.AutoLevelerEnabled = false;
        config.AntiClipperEnabled = false;
        config.BpmDetectorEnabled = false;

        logger!.LogEvent("Session reset (channels, buses and slots removed)", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Clean shutdown
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cleanly shuts the application down: stops every module in reverse startup
    /// order, optionally saves the current session and closes the logger. It is
    /// idempotent: repeated calls (for example one from Q and one from the
    /// ProcessExit handler) do nothing after the first.
    /// </summary>
    /// <param name="saveConfig">
    /// If <c>true</c>, saves the current <see cref="SessionConfig"/> as
    /// <c>last_session.json</c> before closing. This lets the next startup restore
    /// exactly where it was left.
    /// </param>
    public void Shutdown(bool saveConfig)
    {
        lock (shutdownLock)
        {
            if (isShutdown)
            {
                return;
            }
            isShutdown = true;
        }

        try
        {
            bpmDetector?.Stop();
            antiClipper?.Stop();
            autoLeveler?.Stop();
            delayController?.Stop();
            meterReader?.StopReading();
            oscClient?.Disconnect();
            simulator?.Stop();
        }
        catch
        {
            // During shutdown, ignore errors: the priority is to exit cleanly.
        }

        if (saveConfig)
        {
            try
            {
                SaveCurrentSession();
            }
            catch
            {
            }
        }

        try
        {
            logger?.Close();
        }
        catch
        {
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Persistence: save and load
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the current session to <see cref="AppPaths.LastSessionFile"/>. Called
    /// automatically by <see cref="Shutdown"/> when <c>saveConfig=true</c>.
    /// </summary>
    public void SaveCurrentSession()
    {
        // The simulation demo is throwaway: never overwrite the user's real saved
        // session with it.
        if (simulationMode)
        {
            return;
        }
        SyncWatchedDevicesToConfig();
        WriteSession(config, AppPaths.LastSessionFile);
    }

    /// <summary>
    /// Refreshes the persistent watched-device descriptors from the live devices, so
    /// runtime changes — notably the target levels captured by Recalibrate — are saved.
    /// Without this the descriptors would keep the values from when each device was
    /// first added, losing any recalibration on the next launch.
    /// </summary>
    private void SyncWatchedDevicesToConfig()
    {
        if (meterReader == null)
        {
            return;
        }

        List<AudioDeviceDescriptor> descriptors = new List<AudioDeviceDescriptor>();
        foreach (AudioDevice device in meterReader.Devices)
        {
            descriptors.Add(AudioDeviceDescriptor.FromAudioDevice(device));
        }
        config.WatchedDevices = descriptors;
    }

    /// <summary>
    /// Saves the current session as a named preset in the presets folder. The user
    /// can restore it later with <see cref="LoadPresetFrom"/>.
    /// </summary>
    public void SavePresetAs(string presetName)
    {
        SyncWatchedDevicesToConfig();
        WriteSession(config, AppPaths.GetPresetPath(presetName));
    }

    /// <summary>
    /// Loads a preset from the presets folder and applies its full configuration,
    /// replacing the device list, the modules' parameters and the delay slots.
    /// Keeps the on/off state of the modules that were already running.
    /// </summary>
    /// <exception cref="FileNotFoundException">If the preset does not exist.</exception>
    /// <exception cref="InvalidDataException">If the JSON is corrupt.</exception>
    public void LoadPresetFrom(string presetName)
    {
        string path = AppPaths.GetPresetPath(presetName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Preset not found: {presetName}");
        }

        string json = File.ReadAllText(path);
        SessionConfig loaded = JsonSerializer.Deserialize<SessionConfig>(json, JsonReadOptions)
            ?? throw new InvalidDataException("Empty or invalid preset.");

        ApplyLoadedConfig(loaded);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private helper methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the last session's configuration from disk. If the file does not
    /// exist or is corrupt, returns an empty configuration with
    /// <see cref="SessionConfig"/>'s defaults.
    /// </summary>
    private SessionConfig LoadConfigOrDefaults()
    {
        if (!File.Exists(AppPaths.LastSessionFile))
        {
            return new SessionConfig();
        }

        try
        {
            string json = File.ReadAllText(AppPaths.LastSessionFile);
            SessionConfig? loaded = JsonSerializer.Deserialize<SessionConfig>(json, JsonReadOptions);
            if (loaded == null)
            {
                return new SessionConfig();
            }
            // Repair any out-of-range value so module construction cannot throw on a
            // hand-edited or corrupt file.
            loaded.Sanitize();
            return loaded;
        }
        catch
        {
            return new SessionConfig();
        }
    }

    /// <summary>
    /// Builds the list of <see cref="AudioDevice"/> from the configuration's
    /// descriptors. Each descriptor materializes as a <see cref="MixerChannel"/> or
    /// a <see cref="Bus"/> based on its type.
    /// </summary>
    private List<AudioDevice> BuildDevicesFromConfig()
    {
        return BuildDevices(config);
    }

    /// <summary>
    /// Materializes the watched-device list from any <see cref="SessionConfig"/>'s
    /// descriptors. Static so it can build the devices for a configuration being
    /// loaded before that configuration becomes the live one.
    /// </summary>
    private static List<AudioDevice> BuildDevices(SessionConfig cfg)
    {
        List<AudioDevice> devices = new List<AudioDevice>();
        foreach (AudioDeviceDescriptor descriptor in cfg.WatchedDevices)
        {
            devices.Add(descriptor.ToAudioDevice());
        }
        return devices;
    }

    /// <summary>
    /// Finds the BpmDetector's target channel in the device list by the OSC number
    /// saved in the configuration. Returns <c>null</c> if it is not present or if
    /// the configuration says there is no assigned channel (number 0).
    /// </summary>
    private MixerChannel? FindBpmTargetChannel(List<AudioDevice> devices)
    {
        return FindBpmTargetChannel(devices, config);
    }

    /// <summary>
    /// Overload that resolves the BpmDetector's target channel against an arbitrary
    /// configuration, so a session being loaded can be resolved before it becomes
    /// the live one.
    /// </summary>
    private static MixerChannel? FindBpmTargetChannel(List<AudioDevice> devices, SessionConfig cfg)
    {
        if (cfg.BpmTargetChannelOscNumber <= 0)
        {
            return null;
        }

        foreach (AudioDevice device in devices)
        {
            if (device is MixerChannel channel && channel.OscNumber == cfg.BpmTargetChannelOscNumber)
            {
                return channel;
            }
        }
        return null;
    }

    /// <summary>
    /// Applies the delay slots configured in <see cref="SessionConfig"/> to the
    /// <see cref="DelayController"/>. Called after initializing the controller.
    /// </summary>
    private void ApplyDelaySlotsFromConfig()
    {
        foreach (DelaySlotDescriptor descriptor in config.DelaySlots)
        {
            DelaySlotConfig slotConfig;
            if (descriptor.IsAutomatic)
            {
                slotConfig = DelaySlotConfig.Automatic(descriptor.EffectType, descriptor.Beat);
            }
            else
            {
                slotConfig = DelaySlotConfig.Manual(descriptor.EffectType, descriptor.ManualMs);
            }
            delayController!.ConfigureSlot(descriptor.Slot, slotConfig);
        }
    }

    /// <summary>
    /// Applies an already-loaded <see cref="SessionConfig"/> to the live program
    /// state: rebuilds AutoLeveler, AntiClipper and BpmDetector with the new
    /// parameters, replaces the MeterReader's device list, and keeps the on/off
    /// state of the modules that were already running. This is the core operation
    /// of <see cref="LoadPresetFrom"/>.
    /// </summary>
    private void ApplyLoadedConfig(SessionConfig loaded)
    {
        // Repair any out-of-range value first, so the constructors below cannot
        // throw on a hand-edited or corrupt preset.
        loaded.Sanitize();

        // A preset carries the show's mixing config (channels, modules, delays), not
        // the venue's network. Keep the live connection settings so loading a preset
        // never points the UI at a different console than the one we are actually
        // talking to — and the ports cannot change at runtime anyway, since the
        // sockets are already bound. The mixer IP is still changed deliberately via
        // the Preferences menu (which does reconnect).
        loaded.MixerIp = config.MixerIp;
        loaded.MixerSendPort = config.MixerSendPort;
        loaded.MixerReceivePort = config.MixerReceivePort;

        // Build everything the new configuration needs BEFORE touching any live
        // state. If construction were ever to fail, the running session is left
        // completely untouched instead of half-switched between old and new.
        List<AudioDevice> newDevices = BuildDevices(loaded);

        List<MixerChannel> channelsOnly = new List<MixerChannel>();
        foreach (AudioDevice device in newDevices)
        {
            if (device is MixerChannel channel)
            {
                channelsOnly.Add(channel);
            }
        }

        AutoLeveler newAutoLeveler = new AutoLeveler(
            oscClient!, meterReader!, logger!, newDevices,
            loaded.AutoLevelerThresholdDb,
            loaded.AutoLevelerSlewUpDbPerSec,
            loaded.AutoLevelerSlewDownDbPerSec,
            loaded.AutoLevelerGateThresholdDb,
            loaded.AutoLevelerDropLimitDb,
            loaded.AutoLevelerCycleIntervalMs);

        AntiClipper newAntiClipper = new AntiClipper(
            oscClient!, meterReader!, logger!, channelsOnly,
            loaded.AntiClipperClipThresholdDb,
            loaded.AntiClipperReductionStepDb,
            loaded.AntiClipperMaxReductionStepDb,
            loaded.AntiClipperSevereOvershootDb,
            loaded.AntiClipperConfirmationSamples,
            loaded.AntiClipperCooldownMs);

        MixerChannel? bpmTarget = FindBpmTargetChannel(newDevices, loaded);
        MixerChannel effectiveTarget = bpmTarget ?? new MixerChannel("(unassigned)", 1, 0, 0);

        BpmDetector newBpmDetector = new BpmDetector(
            meterReader!, delayController!, logger!,
            effectiveTarget,
            loaded.BpmDetectorMode,
            loaded.BpmTransientThresholdDb,
            loaded.BpmChangeHysteresis);

        // Everything built successfully: switch over to the new session.
        bool autoLevelerWasOn = autoLeveler!.IsEnabled;
        bool antiClipperWasOn = antiClipper!.IsEnabled;
        bool bpmDetectorWasOn = bpmDetector!.IsEnabled;

        autoLeveler.Stop();
        antiClipper.Stop();
        bpmDetector.Stop();

        config = loaded;
        meterReader!.ReplaceDevices(newDevices);

        autoLeveler = newAutoLeveler;
        antiClipper = newAntiClipper;
        bpmDetector = newBpmDetector;

        foreach (int slot in delayController!.DelaySlots.Keys.ToList())
        {
            delayController.ClearSlot(slot);
        }
        ApplyDelaySlotsFromConfig();

        if (autoLevelerWasOn || loaded.AutoLevelerEnabled)
        {
            autoLeveler.Start();
        }
        if (antiClipperWasOn || loaded.AntiClipperEnabled)
        {
            antiClipper.Monitor();
        }
        if (bpmDetectorWasOn || loaded.BpmDetectorEnabled)
        {
            bpmDetector.Start();
        }
    }
}
