using M32LiveConsoleTool.Domain;

namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Full configuration of a working session. This is the object serialized to
/// JSON on save and deserialized on load. It holds everything needed to resume a
/// show exactly as it was: network, watched devices, each module's parameters,
/// configured delay slots and enabled state.
/// </summary>
public class SessionConfig
{
    // ─────────────────────────────────────────────────────────────────────
    // Console connection
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Placeholder mixer IP shown as the default; the user sets the real one in the wizard.</summary>
    public const string DefaultMixerIp = "192.168.1.5";

    /// <summary>Default UDP port the M32 listens on for incoming OSC commands.</summary>
    public const int DefaultSendPort = 10023;

    /// <summary>Default UDP port the app listens on for the M32's replies and meters.</summary>
    public const int DefaultReceivePort = 10024;

    public string MixerIp { get; set; } = DefaultMixerIp;
    public int MixerSendPort { get; set; } = DefaultSendPort;
    public int MixerReceivePort { get; set; } = DefaultReceivePort;

    /// <summary>
    /// Validates that a string is a usable mixer address: a parseable IPv4 or IPv6
    /// literal. The M32 is always reached at a fixed IP on the local network, so a
    /// hostname is intentionally rejected to catch typos that would otherwise
    /// silently stop meters from arriving with no visible reason.
    /// </summary>
    public static bool IsValidMixerIp(string? ip)
    {
        return !string.IsNullOrWhiteSpace(ip)
            && System.Net.IPAddress.TryParse(ip.Trim(), out _);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Watched devices
    // ─────────────────────────────────────────────────────────────────────

    public List<AudioDeviceDescriptor> WatchedDevices { get; set; } = new();

    // ─────────────────────────────────────────────────────────────────────
    // MeterReader
    // ─────────────────────────────────────────────────────────────────────

    public int MeterWindowMs { get; set; } = 2500;

    // ─────────────────────────────────────────────────────────────────────
    // AutoLeveler
    // ─────────────────────────────────────────────────────────────────────

    public bool AutoLevelerEnabled { get; set; } = false;
    public double AutoLevelerThresholdDb { get; set; } = 3.0;
    public double AutoLevelerSlewUpDbPerSec { get; set; } = 0.5;
    public double AutoLevelerSlewDownDbPerSec { get; set; } = 0.5;
    public double AutoLevelerGateThresholdDb { get; set; } = -40.0;
    public double AutoLevelerDropLimitDb { get; set; } = 8.0;
    public int AutoLevelerCycleIntervalMs { get; set; } = 100;

    // ─────────────────────────────────────────────────────────────────────
    // AntiClipper
    // ─────────────────────────────────────────────────────────────────────

    public bool AntiClipperEnabled { get; set; } = false;
    public double AntiClipperClipThresholdDb { get; set; } = -1.0;
    public double AntiClipperReductionStepDb { get; set; } = 3.0;
    public double AntiClipperMaxReductionStepDb { get; set; } = 6.0;
    public double AntiClipperSevereOvershootDb { get; set; } = 6.0;
    public int AntiClipperConfirmationSamples { get; set; } = 2;
    public int AntiClipperCooldownMs { get; set; } = 2000;

    // ─────────────────────────────────────────────────────────────────────
    // BpmDetector
    // ─────────────────────────────────────────────────────────────────────

    public bool BpmDetectorEnabled { get; set; } = false;
    public BpmDetectionMode BpmDetectorMode { get; set; } = BpmDetectionMode.ManualTap;

    /// <summary>OSC number of the target channel. 0 = no channel assigned (ManualTap mode).</summary>
    public int BpmTargetChannelOscNumber { get; set; } = 0;

    public double BpmTransientThresholdDb { get; set; } = -20.0;
    public double BpmChangeHysteresis { get; set; } = 1.0;

    // ─────────────────────────────────────────────────────────────────────
    // DelayController
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configured delay slots. Each entry is described by its number, mode
    /// (auto/manual), rhythmic figure (if auto) and milliseconds (if manual).
    /// </summary>
    public List<DelaySlotDescriptor> DelaySlots { get; set; } = new();

    /// <summary>
    /// Clamps every numeric field to the range its module constructor accepts, so a
    /// configuration loaded from disk can never make those constructors throw. A
    /// hand-edited or corrupt file is repaired in place — each out-of-range value is
    /// reset to a safe default or clamped to the nearest valid bound — instead of
    /// crashing the app or leaving it half-initialized while switching sessions.
    /// </summary>
    /// <remarks>
    /// The valid ranges mirror the validation in the module constructors
    /// (<see cref="Modules.AutoLeveler"/>, <see cref="Modules.AntiClipper"/>,
    /// <see cref="Modules.BpmDetector"/>). Defaults come from a fresh
    /// <see cref="SessionConfig"/>.
    /// </remarks>
    public void Sanitize()
    {
        SessionConfig defaults = new SessionConfig();

        // Network ports.
        if (MixerSendPort < 1 || MixerSendPort > 65535) MixerSendPort = defaults.MixerSendPort;
        if (MixerReceivePort < 1 || MixerReceivePort > 65535) MixerReceivePort = defaults.MixerReceivePort;

        // MeterReader window.
        if (MeterWindowMs < 100 || MeterWindowMs > 10000) MeterWindowMs = defaults.MeterWindowMs;

        // AutoLeveler: the constructor only requires these to be strictly positive
        // (and the cycle interval within 10..5000 ms).
        if (AutoLevelerThresholdDb <= 0) AutoLevelerThresholdDb = defaults.AutoLevelerThresholdDb;
        if (AutoLevelerSlewUpDbPerSec <= 0) AutoLevelerSlewUpDbPerSec = defaults.AutoLevelerSlewUpDbPerSec;
        if (AutoLevelerSlewDownDbPerSec <= 0) AutoLevelerSlewDownDbPerSec = defaults.AutoLevelerSlewDownDbPerSec;
        if (AutoLevelerDropLimitDb <= 0) AutoLevelerDropLimitDb = defaults.AutoLevelerDropLimitDb;
        AutoLevelerCycleIntervalMs = Math.Clamp(AutoLevelerCycleIntervalMs, 10, 5000);

        // AntiClipper. The aggressive step must be >= the normal step, so clamp the
        // normal one first and use it as the lower bound for the aggressive one.
        AntiClipperClipThresholdDb = Math.Clamp(AntiClipperClipThresholdDb, -20.0, 0.0);
        AntiClipperReductionStepDb = Math.Clamp(AntiClipperReductionStepDb, 0.1, 12.0);
        AntiClipperMaxReductionStepDb = Math.Clamp(AntiClipperMaxReductionStepDb, AntiClipperReductionStepDb, 12.0);
        AntiClipperSevereOvershootDb = Math.Clamp(AntiClipperSevereOvershootDb, 1.0, 18.0);
        AntiClipperConfirmationSamples = Math.Clamp(AntiClipperConfirmationSamples, 1, 20);
        AntiClipperCooldownMs = Math.Clamp(AntiClipperCooldownMs, 100, 30000);

        // BpmDetector.
        BpmTransientThresholdDb = Math.Clamp(BpmTransientThresholdDb, -60.0, 0.0);
        BpmChangeHysteresis = Math.Clamp(BpmChangeHysteresis, 0.0, 20.0);

        // Repair the watched-device list (drop unknown types, out-of-range numbers
        // and duplicates) and drop duplicate delay slots. These can only come from a
        // hand-edited file; the live menus already prevent them.
        SanitizeWatchedDevices();
        DeduplicateDelaySlots();
    }

    /// <summary>
    /// Keeps only watched devices with a known type ("channel" or "bus") and an
    /// in-range OSC number (channels 1-32, buses 1-16), dropping duplicates by
    /// (type, number) and keeping the first valid occurrence. An out-of-range channel
    /// would otherwise throw while subscribing (crashing startup); a duplicate would
    /// create an orphan device whose subscription alias collides.
    /// </summary>
    private void SanitizeWatchedDevices()
    {
        HashSet<string> seen = new HashSet<string>();
        List<AudioDeviceDescriptor> valid = new List<AudioDeviceDescriptor>();
        foreach (AudioDeviceDescriptor d in WatchedDevices)
        {
            bool isChannel = d.Type == "channel";
            bool isBus = d.Type == "bus";
            if (!isChannel && !isBus)
            {
                continue;
            }

            int maxNumber = isChannel ? 32 : 16;
            if (d.OscNumber < 1 || d.OscNumber > maxNumber)
            {
                continue;
            }

            if (seen.Add($"{d.Type}:{d.OscNumber}"))
            {
                valid.Add(d);
            }
        }
        WatchedDevices = valid;
    }

    /// <summary>
    /// Removes delay slots that repeat a slot number, keeping the first. The
    /// controller keys slots by number, so a duplicate would be applied
    /// ambiguously and then re-saved.
    /// </summary>
    private void DeduplicateDelaySlots()
    {
        HashSet<int> seen = new HashSet<int>();
        List<DelaySlotDescriptor> unique = new List<DelaySlotDescriptor>();
        foreach (DelaySlotDescriptor s in DelaySlots)
        {
            if (seen.Add(s.Slot))
            {
                unique.Add(s);
            }
        }
        DelaySlots = unique;
    }

    /// <summary>
    /// Builds a ready-to-run demo session for simulation mode, so choosing
    /// simulation drops the engineer straight into a populated dashboard instead of
    /// the first-run wizard (which only makes sense when configuring a real
    /// console). A handful of representative channels and buses at the default
    /// target level; the IP is irrelevant because the simulator lives on loopback.
    /// </summary>
    public static SessionConfig CreateSimulationDemo()
    {
        SessionConfig config = new SessionConfig();

        (int osc, string name)[] channels =
        {
            (1, "Kick"), (2, "Snare"), (3, "Bass"),
            (4, "Elec Guitar"), (5, "Ac. Guitar"), (6, "Lead Vocal"),
            (7, "Click")
        };
        foreach ((int osc, string name) in channels)
        {
            config.WatchedDevices.Add(new AudioDeviceDescriptor
            {
                Type = "channel",
                Name = name,
                OscNumber = osc,
                TargetLevelDb = -18.0,
                PreampGain = 0.0
            });
        }

        (int osc, string name)[] buses =
        {
            (1, "Drums"), (2, "Monitors")
        };
        foreach ((int osc, string name) in buses)
        {
            config.WatchedDevices.Add(new AudioDeviceDescriptor
            {
                Type = "bus",
                Name = name,
                OscNumber = osc,
                TargetLevelDb = -18.0
            });
        }

        return config;
    }
}

/// <summary>
/// Serializable descriptor of a configured delay slot.
/// </summary>
public class DelaySlotDescriptor
{
    public int Slot { get; set; }
    public DelayEffectType EffectType { get; set; }
    public bool IsAutomatic { get; set; }
    public BeatType Beat { get; set; }
    public double ManualMs { get; set; }
}
