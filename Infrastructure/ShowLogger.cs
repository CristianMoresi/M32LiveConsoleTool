namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Event logger for the show. Writes every important action to a text file and
/// keeps a ring buffer of the most recent events in memory so the UI can show
/// them live on the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Visibility policy</strong>: the in-memory ring keeps all events
/// (Info, Warning, Error) up to its capacity. The UI queries only the important
/// ones (Warning and Error) via <see cref="GetRecentImportantEvents"/> so the
/// dashboard is not flooded with routine messages. The file on disk receives
/// every event that reaches the logger.
/// </para>
/// </remarks>
public class ShowLogger
{
    public const int RECENT_EVENTS_CAPACITY = 50;

    private string logPath;
    private StreamWriter? writer;
    private bool isOpen;
    private readonly LinkedList<LogEntry> recentEvents;
    private readonly object writeLock = new object();

    public ShowLogger()
    {
        this.logPath = "";
        this.writer = null;
        this.isOpen = false;
        this.recentEvents = new LinkedList<LogEntry>();
    }

    public string LogPath
    {
        get { return logPath; }
    }

    public bool IsOpen
    {
        get { return isOpen; }
    }

    public void Open(string path)
    {
        try
        {
            writer = new StreamWriter(path, append: true);
            logPath = path;
            isOpen = true;

            writer.WriteLine();
            writer.WriteLine($"========== Session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
            writer.Flush();
        }
        catch
        {
            // We cannot write to the Console here: the dashboard owns the screen.
            // Disk logging is disabled, but the in-memory ring keeps working.
            writer = null;
            isOpen = false;
        }
    }

    public void LogEvent(string message, Severity severity)
    {
        DateTime now = DateTime.Now;
        LogEntry entry = new LogEntry(now, severity, message);

        lock (writeLock)
        {
            recentEvents.AddLast(entry);
            while (recentEvents.Count > RECENT_EVENTS_CAPACITY)
            {
                recentEvents.RemoveFirst();
            }

            if (isOpen && writer != null)
            {
                string severityTag = severity.ToString().ToUpper().PadRight(7);
                string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{severityTag}] {message}";

                try
                {
                    writer.WriteLine(line);
                    writer.Flush();
                }
                catch
                {
                    // Ignore write errors so we never interfere with the dashboard.
                }
            }
        }
    }

    /// <summary>
    /// Returns the most recent events filtered to Warning and Error only. Info
    /// events are omitted because they are routine and would flood the UI.
    /// Returns up to <paramref name="maxCount"/> events in chronological order
    /// (oldest first, newest last).
    /// </summary>
    public List<LogEntry> GetRecentImportantEvents(int maxCount)
    {
        lock (writeLock)
        {
            List<LogEntry> important = new List<LogEntry>();

            foreach (LogEntry entry in recentEvents)
            {
                if (entry.Severity == Severity.Warning || entry.Severity == Severity.Error)
                {
                    important.Add(entry);
                }
            }

            if (important.Count <= maxCount)
            {
                return important;
            }

            // Return only the last maxCount.
            return important.GetRange(important.Count - maxCount, maxCount);
        }
    }

    public void Close()
    {
        lock (writeLock)
        {
            if (writer != null)
            {
                try
                {
                    writer.WriteLine($"========== Session ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
                    writer.Flush();
                    writer.Close();
                }
                catch
                {
                }

                writer = null;
            }

            isOpen = false;
        }
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; }
    public Severity Severity { get; }
    public string Message { get; }

    public LogEntry(DateTime timestamp, Severity severity, string message)
    {
        Timestamp = timestamp;
        Severity = severity;
        Message = message;
    }
}
