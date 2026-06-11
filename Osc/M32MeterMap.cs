namespace M32LiveConsoleTool.Osc;

/// <summary>
/// OSC addresses and sizes of the M32 meter bundles used by this project, plus
/// the subscription-alias constants the app shares between modules.
/// </summary>
/// <remarks>
/// <para>
/// A "bundle" in OSC is a prefab packet of measurements the M32 can stream
/// continuously once a client subscribes to it. Each bundle has a fixed OSC
/// address (for example <c>/meters/2</c>) and always returns the same number of
/// floats in the same order, defined by the manufacturer.
/// </para>
/// <para>
/// The alias constants (prefix, channel infix, buses suffix) live here because
/// they are shared between <see cref="Modules.MeterReader"/> and
/// <see cref="Modules.AntiClipper"/>: both modules need to know the alias format
/// the app uses to recognize incoming messages that concern them.
/// </para>
/// </remarks>
public static class M32Buckets
{
    /// <summary>
    /// Address of the bus/matrix/main/mono page bundle. A single subscription to
    /// this bundle covers every bus on the console at once.
    /// </summary>
    public const string MIX_BUS_PAGE = "/meters/2";

    /// <summary>Total number of floats returned by the bus-page bundle.</summary>
    public const int MIX_BUS_PAGE_FLOAT_COUNT = 49;

    /// <summary>
    /// Address of the channel-strip inspection bundle. Subscribing to this bundle
    /// brings the 4 internal floats of ONE individual channel.
    /// </summary>
    public const string CHANNEL_STRIP = "/meters/6";

    /// <summary>Total number of floats returned by the channel-strip bundle.</summary>
    public const int CHANNEL_STRIP_FLOAT_COUNT = 4;

    // ─────────────────────────────────────────────────────────────────────
    // App subscription aliases
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Common prefix for every subscription alias this app registers. Ensures
    /// they do not clash with aliases from other clients on the same console and
    /// lets any module recognize that an incoming OSC message belongs to this app.
    /// </summary>
    public const string APP_ALIAS_PREFIX = "/m32lct_";

    /// <summary>
    /// Infix added to the prefix to build a specific channel's alias. The full
    /// alias is <c>APP_ALIAS_PREFIX + CHANNEL_ALIAS_INFIX + channelNumber</c> with the
    /// channel number as two digits. Example: <c>/m32lct_ch05</c>.
    /// </summary>
    public const string CHANNEL_ALIAS_INFIX = "ch";

    /// <summary>
    /// Suffix of the single bus-page subscription alias. Combined with the prefix
    /// it yields <c>/m32lct_buses</c>.
    /// </summary>
    public const string BUSES_ALIAS_SUFFIX = "buses";

    /// <summary>
    /// Converts the user-facing channel number (1..32) to the internal id the
    /// console expects when subscribing to the <see cref="CHANNEL_STRIP"/> bundle
    /// (0..31). The M32 shows channels 1-32 to the engineer but addresses them
    /// from zero internally over OSC.
    /// </summary>
    public static int UserChannelToOscId(int userChannelNumber)
    {
        if (userChannelNumber < 1 || userChannelNumber > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(userChannelNumber),
                $"Channel number must be between 1 and 32 (got: {userChannelNumber}).");
        }

        return userChannelNumber - 1;
    }

    /// <summary>
    /// Builds a channel's full alias from its user-facing number (1..32).
    /// Example: 5 → "/m32lct_ch05".
    /// </summary>
    public static string BuildChannelAlias(int userChannelNumber)
    {
        return $"{APP_ALIAS_PREFIX}{CHANNEL_ALIAS_INFIX}{userChannelNumber:D2}";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Head amp (preamp) addressing and ranges
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Minimum head amp gain in dB. The OSC value is normalized over this range.</summary>
    public const double HEAD_AMP_GAIN_MIN_DB = -12.0;

    /// <summary>Maximum head amp gain in dB.</summary>
    public const double HEAD_AMP_GAIN_MAX_DB = 60.0;

    /// <summary>
    /// Gain step the console quantizes the head amp to (linf [-12, 60, 0.5]).
    /// Values written between steps are rounded by the console, so the app
    /// quantizes locally too and its state matches the console exactly.
    /// </summary>
    public const double HEAD_AMP_GAIN_STEP_DB = 0.5;

    /// <summary>Minimum channel digital trim in dB (used when the source has no head amp).</summary>
    public const double CHANNEL_TRIM_MIN_DB = -18.0;

    /// <summary>Maximum channel digital trim in dB.</summary>
    public const double CHANNEL_TRIM_MAX_DB = 18.0;

    /// <summary>Trim step the console quantizes the digital trim to (linf [-18, 18, 0.25]).</summary>
    public const double CHANNEL_TRIM_STEP_DB = 0.25;

    /// <summary>Value of <c>/-ha/NN/index</c> meaning "no analog head amp" (digital/internal source).</summary>
    public const int NO_HEAD_AMP = -1;

    /// <summary>
    /// Address that asks the console which head amp feeds an input. The reply
    /// <c>/-ha/NN/index ,i &lt;haIndex&gt;</c> gives the head amp index (or -1 for
    /// a digital source). The input index is 0-based: 0..31 = channels 1..32.
    /// </summary>
    public static string BuildHeadAmpIndexAddress(int inputIndex)
    {
        return $"/-ha/{inputIndex:D2}/index";
    }

    /// <summary>
    /// Gain address of a head amp by its index (0-127). Covers local XLR inputs
    /// (0-31) and AES50 stage boxes (32-127) transparently.
    /// </summary>
    public static string BuildHeadAmpGainAddress(int headAmpIndex)
    {
        return $"/headamp/{headAmpIndex:D3}/gain";
    }

    /// <summary>Digital trim address of a channel, used when it has no analog head amp.</summary>
    public static string BuildChannelTrimAddress(int userChannelNumber)
    {
        return $"/ch/{userChannelNumber:D2}/preamp/trim";
    }
}

/// <summary>
/// Positions of each measurement inside the <c>/meters/2</c> blob (bus, matrix,
/// main and mono page).
/// </summary>
public enum MixBusFloat
{
    Bus1 = 0, Bus2 = 1, Bus3 = 2, Bus4 = 3,
    Bus5 = 4, Bus6 = 5, Bus7 = 6, Bus8 = 7,
    Bus9 = 8, Bus10 = 9, Bus11 = 10, Bus12 = 11,
    Bus13 = 12, Bus14 = 13, Bus15 = 14, Bus16 = 15,

    Matrix1 = 16, Matrix2 = 17, Matrix3 = 18,
    Matrix4 = 19, Matrix5 = 20, Matrix6 = 21,

    MainL = 22,
    MainR = 23,
    Mono = 24
}

/// <summary>
/// Positions of each measurement inside the <c>/meters/6</c> blob (one channel's
/// internal processing chain).
/// </summary>
public enum ChannelStripFloat
{
    PostTrim = 0,
    GateGainReduction = 1,
    CompressorGainReduction = 2,
    PostFader = 3
}
