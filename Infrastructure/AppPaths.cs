using System.Diagnostics;
using System.Runtime.InteropServices;

namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Centralizes the paths of the app's persistent files (configuration, presets,
/// logs) and abstracts the differences between operating systems. User files are
/// always stored in the system's standard application-data folder, not next to
/// the executable.
/// </summary>
/// <remarks>
/// <para>
/// Per-OS conventions:
/// </para>
/// <list type="bullet">
///   <item>Windows: <c>%APPDATA%\M32LiveConsoleTool\</c>
///         (i.e. <c>C:\Users\&lt;user&gt;\AppData\Roaming\M32LiveConsoleTool\</c>).</item>
///   <item>macOS: <c>~/Library/Application Support/M32LiveConsoleTool/</c>.</item>
///   <item>Linux: <c>~/.config/M32LiveConsoleTool/</c> (XDG Base Directory convention).</item>
/// </list>
/// <para>
/// <see cref="Environment.GetFolderPath"/> with
/// <see cref="Environment.SpecialFolder.ApplicationData"/> returns the correct
/// path on each system, with no need to detect the OS manually.
/// </para>
/// </remarks>
public static class AppPaths
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Subfolder name inside the user's application-data directory.</summary>
    public const string APP_FOLDER_NAME = "M32LiveConsoleTool";

    /// <summary>Subfolder where configuration presets are stored.</summary>
    public const string PRESETS_SUBFOLDER = "presets";

    /// <summary>Subfolder where per-session log files are stored.</summary>
    public const string LOGS_SUBFOLDER = "logs";

    /// <summary>File name of the last-session configuration.</summary>
    public const string LAST_SESSION_FILENAME = "last_session.json";

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Root application directory for the current user. On Windows this returns
    /// something like <c>C:\Users\Cristian\AppData\Roaming\M32LiveConsoleTool</c>.
    /// </summary>
    public static string AppDirectory
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, APP_FOLDER_NAME);
        }
    }

    /// <summary>Directory where session presets are stored.</summary>
    public static string PresetsDirectory
    {
        get { return Path.Combine(AppDirectory, PRESETS_SUBFOLDER); }
    }

    /// <summary>Directory where per-session log files are stored.</summary>
    public static string LogsDirectory
    {
        get { return Path.Combine(AppDirectory, LOGS_SUBFOLDER); }
    }

    /// <summary>Absolute path of the most recent session file.</summary>
    public static string LastSessionFile
    {
        get { return Path.Combine(AppDirectory, LAST_SESSION_FILENAME); }
    }

    /// <summary>
    /// Builds the path of a fresh log file for the current session, named by the
    /// local date and time so every launch writes its own file under
    /// <see cref="LogsDirectory"/> instead of appending to one ever-growing file.
    /// </summary>
    public static string GetSessionLogPath()
    {
        string filename = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";
        return Path.Combine(LogsDirectory, filename);
    }

    /// <summary>Number of recent per-session log files kept; older ones are pruned.</summary>
    public const int MAX_SESSION_LOGS = 50;

    /// <summary>
    /// Deletes the oldest session logs, keeping the most recent
    /// <see cref="MAX_SESSION_LOGS"/>, so the logs folder does not grow forever.
    /// </summary>
    public static void PruneOldSessionLogs()
    {
        PruneOldLogs(LogsDirectory, MAX_SESSION_LOGS);
    }

    /// <summary>
    /// Deletes the oldest <c>*.log</c> files in <paramref name="directory"/>, keeping
    /// the most recent <paramref name="keep"/> by last-write time. Best-effort: never
    /// throws. Exposed (with the directory parameter) so it can be unit-tested.
    /// </summary>
    public static void PruneOldLogs(string directory, int keep)
    {
        try
        {
            if (keep < 0)
            {
                keep = 0;
            }

            DirectoryInfo dir = new DirectoryInfo(directory);
            if (!dir.Exists)
            {
                return;
            }

            IEnumerable<FileInfo> stale = dir.GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);

            foreach (FileInfo file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // A log may be locked (current session); skip it.
                }
            }
        }
        catch
        {
            // Pruning is best-effort; never let it disrupt startup.
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the required folders if they do not exist. Called once at startup,
    /// before any read or write operation.
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(PresetsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// Returns the absolute path of a preset file by its name (without
    /// extension). The file lives in the presets folder.
    /// </summary>
    public static string GetPresetPath(string presetName)
    {
        string trimmed = (presetName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("The preset name cannot be empty.", nameof(presetName));
        }

        // Reject path separators and other invalid filename characters so a preset
        // name can never escape the presets folder (no path traversal, no absolute
        // paths). On each OS, GetInvalidFileNameChars includes that platform's
        // directory separators.
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The preset name contains invalid characters.", nameof(presetName));
        }

        string filename = trimmed;
        if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".json";
        }
        return Path.Combine(PresetsDirectory, filename);
    }

    /// <summary>
    /// Opens the system file explorer at the app's data folder. Uses Explorer on
    /// Windows, Finder on macOS and xdg-open on Linux. Does not throw on failure:
    /// the caller can check the return value.
    /// </summary>
    /// <returns><c>true</c> if it could be opened; <c>false</c> if it failed.</returns>
    public static bool OpenAppDirectoryInExplorer()
    {
        try
        {
            EnsureDirectoriesExist();
            string path = AppDirectory;

            ProcessStartInfo psi;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi = new ProcessStartInfo("explorer.exe", $"\"{path}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi = new ProcessStartInfo("open", $"\"{path}\"");
            }
            else
            {
                // Linux and other Unix.
                psi = new ProcessStartInfo("xdg-open", $"\"{path}\"");
            }

            psi.UseShellExecute = false;
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
