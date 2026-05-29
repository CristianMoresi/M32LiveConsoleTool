using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Ui;

/// <summary>
/// First-run wizard. Runs when the program starts and finds no previous
/// configuration. Asks the engineer for the minimum data to get going (mixer IP,
/// OSC ports, channels and buses to watch with their names) and builds a
/// <see cref="SessionConfig"/> from the answers.
/// </summary>
/// <remarks>
/// <para>
/// The wizard runs before the dashboard, in normal console mode, using
/// <see cref="Console.WriteLine"/> and <see cref="Console.ReadLine"/>. It uses no
/// ANSI coordinates or alternate screen: it is a sequential, line-by-line flow,
/// which is more comfortable for entering long values (such as an IP) than the
/// dashboard's submenu system.
/// </para>
/// <para>
/// Every question has a default in brackets: the engineer can just press Enter to
/// accept it. The defaults come from <see cref="SessionConfig"/> (a placeholder
/// IP and the standard OSC ports 10023 send / 10024 receive).
/// </para>
/// <para>
/// The channel and bus list uses a friendly syntax: numbers separated by commas
/// (<c>1,3,5</c>) or ranges with a dash (<c>1-4,7</c>). The parser accepts any
/// combination and discards duplicates. After collecting the numbers, it asks for
/// a name for each (defaulting to <c>Channel N</c> or <c>Bus N</c>).
/// </para>
/// </remarks>
public static class FirstRunWizard
{
    // ─────────────────────────────────────────────────────────────────────
    // Public constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initial target level in decibels for new devices. -18 dBFS is the
    /// professional standard for live audio: it leaves enough headroom (18 dB)
    /// for transients without clipping.
    /// </summary>
    public const double DEFAULT_TARGET_LEVEL_DB = -18.0;

    // ─────────────────────────────────────────────────────────────────────
    // Main public method
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full wizard flow: shows the banner, asks for the data one item at
    /// a time with defaults, shows a summary at the end and waits for the engineer
    /// to confirm before continuing.
    /// </summary>
    /// <returns>Configuration built from the answers.</returns>
    public static SessionConfig Run()
    {
        PrintBanner();

        SessionConfig config = new SessionConfig();

        config.MixerIp = AskIp(
            "M32 mixer IP",
            SessionConfig.DefaultMixerIp);

        config.MixerSendPort = AskInt(
            "Send port (where the console listens)",
            SessionConfig.DefaultSendPort,
            1,
            65535);

        config.MixerReceivePort = AskInt(
            "Receive port (where we listen to the console)",
            SessionConfig.DefaultReceivePort,
            1,
            65535);

        // The send and receive ports must differ: the simulator binds the send port
        // locally, so an equal receive port would collide, and even against real
        // hardware two distinct ports are the standard setup.
        while (config.MixerReceivePort == config.MixerSendPort)
        {
            Console.WriteLine("   The receive port must differ from the send port.");
            config.MixerReceivePort = AskInt(
                "Receive port (where we listen to the console)",
                SessionConfig.DefaultReceivePort,
                1,
                65535);
        }

        Console.WriteLine();
        List<int> channelNumbers = AskNumberList(
            "Enter the channel numbers to follow (comma-separated),\n" +
            " or press 0 to follow none for now",
            1,
            32);

        foreach (int oscNumber in channelNumbers)
        {
            string name = AskName($"Name for channel {oscNumber}", $"Channel {oscNumber}");
            AudioDeviceDescriptor desc = new AudioDeviceDescriptor();
            desc.Type = "channel";
            desc.Name = name;
            desc.OscNumber = oscNumber;
            desc.TargetLevelDb = DEFAULT_TARGET_LEVEL_DB;
            desc.PreampGain = 0.0;
            config.WatchedDevices.Add(desc);
        }

        Console.WriteLine();
        List<int> busNumbers = AskNumberList(
            "Enter the bus numbers to follow (comma-separated),\n" +
            " or press 0 to follow none for now",
            1,
            16);

        foreach (int oscNumber in busNumbers)
        {
            string name = AskName($"Name for bus {oscNumber}", $"Bus {oscNumber}");
            AudioDeviceDescriptor desc = new AudioDeviceDescriptor();
            desc.Type = "bus";
            desc.Name = name;
            desc.OscNumber = oscNumber;
            desc.TargetLevelDb = DEFAULT_TARGET_LEVEL_DB;
            desc.PreampGain = 0.0;
            config.WatchedDevices.Add(desc);
        }

        PrintSummary(config);

        return config;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Banner and summary
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the wizard's banner with a short message explaining why it is
    /// running and how to press Enter to accept the defaults.
    /// </summary>
    private static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine(" Initial configuration");
        Console.WriteLine("=================================================================");
        Console.WriteLine();
        Console.WriteLine(" No previous configuration found. Let's create one.");
        Console.WriteLine(" Press ENTER on any question to accept the suggested value.");
        Console.WriteLine();
    }

    /// <summary>
    /// Prints a summary of the created configuration (IP, ports, number of watched
    /// channels and buses) and waits for the engineer to press Enter before
    /// continuing.
    /// </summary>
    private static void PrintSummary(SessionConfig config)
    {
        Console.WriteLine();
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(" Configuration created:");
        Console.WriteLine($"   Mixer: {config.MixerIp}  send:{config.MixerSendPort}  receive:{config.MixerReceivePort}");

        int channels = 0;
        int buses = 0;
        foreach (AudioDeviceDescriptor d in config.WatchedDevices)
        {
            if (d.Type == "channel") channels++;
            else if (d.Type == "bus") buses++;
        }
        Console.WriteLine($"   Watched channels: {channels}");
        Console.WriteLine($"   Watched buses:    {buses}");
        Console.WriteLine();
        Console.WriteLine(" You can add, remove or rename channels and buses from the menu.");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine();
        Console.Write(" Press ENTER to continue...");
        Console.ReadLine();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Input helpers with defaults
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the user for a string, showing the default in brackets. If the user
    /// presses Enter with no input, returns the default. Used for general text
    /// such as the IP.
    /// </summary>
    private static string AskString(string prompt, string defaultValue)
    {
        Console.Write($" {prompt} [{defaultValue}]: ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            return defaultValue;
        }
        return line.Trim();
    }

    /// <summary>
    /// Asks for the mixer IP, showing the default in brackets, and retries until the
    /// user enters a parseable IP address (or accepts the default with Enter). The IP
    /// is the one piece of data that silently breaks the whole session if mistyped,
    /// so it is validated up front instead of failing later with no visible reason.
    /// </summary>
    private static string AskIp(string prompt, string defaultValue)
    {
        while (true)
        {
            Console.Write($" {prompt} [{defaultValue}]: ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return defaultValue;
            }

            string trimmed = line.Trim();
            if (SessionConfig.IsValidMixerIp(trimmed))
            {
                return trimmed;
            }

            Console.WriteLine("   Invalid IP address. Enter a valid IPv4/IPv6 address (e.g. 192.168.1.5).");
        }
    }

    /// <summary>
    /// Variant of <see cref="AskString"/> with extra indentation for channel and
    /// bus names, which are asked inside a block tied to a number. Same behavior:
    /// Enter accepts the default.
    /// </summary>
    private static string AskName(string prompt, string defaultValue)
    {
        Console.Write($"   {prompt} [{defaultValue}]: ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            return defaultValue;
        }
        return line.Trim();
    }

    /// <summary>
    /// Asks for an integer with a default and a range. Retries until the user
    /// enters a valid value (or presses Enter to accept the default).
    /// </summary>
    /// <param name="prompt">Prompt text.</param>
    /// <param name="defaultValue">Suggested value shown in brackets.</param>
    /// <param name="min">Inclusive lower bound.</param>
    /// <param name="max">Inclusive upper bound.</param>
    private static int AskInt(string prompt, int defaultValue, int min, int max)
    {
        while (true)
        {
            Console.Write($" {prompt} [{defaultValue}]: ");
            string? line = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(line))
            {
                return defaultValue;
            }

            if (int.TryParse(line.Trim(), out int value) && value >= min && value <= max)
            {
                return value;
            }

            Console.WriteLine($"   Invalid value. Enter a number between {min} and {max}.");
        }
    }

    /// <summary>
    /// Asks for a list of numbers with a friendly syntax: numbers separated by
    /// commas and/or ranges with a dash. Accepts "0" or an empty line as "none".
    /// Retries until it gets a valid entry.
    /// </summary>
    /// <remarks>
    /// Valid examples: <c>1,3,5</c>, <c>1-4</c>, <c>1-4,7,10-12</c>. Duplicates are
    /// removed and the list is returned sorted ascending.
    /// </remarks>
    private static List<int> AskNumberList(string prompt, int min, int max)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($" {prompt}");
            Console.WriteLine($"   Example: 1,2,3,5  or  1-4,7  (range {min}-{max})");
            Console.Write("   > ");

            string? line = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(line))
            {
                return new List<int>();
            }

            string trimmed = line.Trim();
            if (trimmed == "0")
            {
                return new List<int>();
            }

            List<int>? parsed = TryParseNumberList(trimmed, min, max);
            if (parsed != null)
            {
                return parsed;
            }

            Console.WriteLine($"   Invalid input. All numbers must be between {min} and {max}.");
        }
    }

    /// <summary>
    /// Tries to parse a string with number-list syntax. Returns the sorted,
    /// duplicate-free list if valid, or <c>null</c> if it finds any malformed or
    /// out-of-range token.
    /// </summary>
    /// <remarks>
    /// Accepts comma, space and semicolon as separators. Accepts ranges with a
    /// dash in any order (<c>5-3</c> equals <c>3-5</c>). The whole input must be
    /// valid: if a single token fails, the entire input is rejected.
    /// </remarks>
    private static List<int>? TryParseNumberList(string input, int min, int max)
    {
        HashSet<int> result = new HashSet<int>();
        string[] tokens = input.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            if (token.Contains('-'))
            {
                string[] parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    return null;
                }
                if (!int.TryParse(parts[0], out int from) || !int.TryParse(parts[1], out int to))
                {
                    return null;
                }
                if (from > to)
                {
                    (from, to) = (to, from);
                }
                for (int i = from; i <= to; i++)
                {
                    if (i < min || i > max)
                    {
                        return null;
                    }
                    result.Add(i);
                }
            }
            else
            {
                if (!int.TryParse(token, out int value))
                {
                    return null;
                }
                if (value < min || value > max)
                {
                    return null;
                }
                result.Add(value);
            }
        }

        List<int> sorted = new List<int>(result);
        sorted.Sort();
        return sorted;
    }
}
