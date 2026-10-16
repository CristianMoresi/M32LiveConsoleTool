using System.Collections.Concurrent;

namespace M32LiveConsoleTool.Infrastructure;

/// <summary>Bounded asynchronous disk logging; the dashboard never waits for disk I/O.</summary>
public class ShowLogger
{
    public const int RECENT_EVENTS_CAPACITY = 50;
    private readonly Queue<LogEntry> recent = new();
    private readonly object sync = new();
    private BlockingCollection<LogEntry>? pending;
    private Thread? worker;
    private volatile bool isOpen;
    private long dropped;
    public string LogPath { get; private set; } = "";
    public bool IsOpen => isOpen;
    public string? DiskError { get; private set; }
    public long DroppedEvents => Interlocked.Read(ref dropped);

    public void Open(string path)
    {
        Close();
        try
        {
            var writer = new StreamWriter(path, append: true);
            var queue = new BlockingCollection<LogEntry>(1024);
            lock (sync) { pending = queue; LogPath = path; isOpen = true; DiskError = null; }
            worker = new Thread(() => WriteLoop(queue, writer)) { IsBackground = true, Name = "Show log" };
            worker.Start();
            LogEvent("Session started", Severity.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiskError = ex.Message;
            LogEvent($"Disk logging unavailable: {ex.Message}", Severity.Warning);
        }
    }

    public void LogEvent(string message, Severity severity)
    {
        var entry = new LogEntry(DateTime.Now, severity, message);
        lock (sync)
        {
            recent.Enqueue(entry);
            while (recent.Count > RECENT_EVENTS_CAPACITY) recent.Dequeue();
            if (isOpen && pending != null && !pending.TryAdd(entry)) Interlocked.Increment(ref dropped);
        }
    }

    public List<LogEntry> GetRecentImportantEvents(int maxCount)
    {
        lock (sync) return recent.Where(x => x.Severity is Severity.Warning or Severity.Error).TakeLast(Math.Max(0, maxCount)).ToList();
    }

    private void WriteLoop(BlockingCollection<LogEntry> queue, StreamWriter writer)
    {
        try
        {
            using (writer)
            {
                while (!queue.IsCompleted)
                {
                    if (queue.TryTake(out var entry, 250))
                    {
                        writer.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Severity}] {entry.Message}");
                        for (int i = 0; i < 127 && queue.TryTake(out entry); i++)
                            writer.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Severity}] {entry.Message}");
                    }
                    writer.Flush();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            isOpen = false;
            DiskError = ex.Message;
            LogEvent($"Disk log stopped: {ex.Message}", Severity.Error);
        }
    }

    public void Close()
    {
        lock (sync)
        {
            if (pending == null) return;
            pending.TryAdd(new LogEntry(DateTime.Now, Severity.Info, "Session ended"));
            pending.CompleteAdding();
            pending = null;
            isOpen = false;
        }
        if (worker != Thread.CurrentThread && worker?.Join(5000) == false) DiskError = "Log flush did not complete within 5 seconds.";
    }
}

public class LogEntry(DateTime timestamp, Severity severity, string message)
{
    public DateTime Timestamp { get; } = timestamp;
    public Severity Severity { get; } = severity;
    public string Message { get; } = message;
}
