using System.Runtime.InteropServices;
using System.Text;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;

namespace M32LiveConsoleTool.Ui;

/// <summary>
/// Adaptive console dashboard. Reads the real window size on every frame and
/// distributes the rows across sections by priority, the way htop or vim do. Uses
/// ANSI sequences on the terminal's alternate screen to paint a fixed UI without
/// touching the console scrollback.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anti-scroll strategy with ANSI sequences</strong>: the dashboard uses
/// the terminal's "alternate screen" (<c>ESC [?1049h</c>), the same technique vim,
/// less and htop use to paint a fixed UI without touching the scrollback. On
/// entering the alternate screen, the console saves the previous content and gives
/// us a clean buffer the exact size of the visible window. On exit
/// (<c>ESC [?1049l</c>), it restores the previous buffer intact.
/// </para>
/// <para>
/// Inside the alternate screen, each frame is painted by placing the cursor at
/// absolute coordinates with <c>ESC [row;col H</c> before each line. The console
/// does not scroll because the alternate buffer is exactly the size of the visible
/// window.
/// </para>
/// <para>
/// <strong>Enabling ANSI on Windows</strong>: on Windows 10+ consoles support ANSI
/// natively but the <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> mode must be turned
/// on through a native call to <c>SetConsoleMode</c> in <c>kernel32.dll</c>.
/// <see cref="EnableAnsi"/> does this at startup. On macOS and Linux ANSI
/// sequences work by default.
/// </para>
/// <para>
/// <strong>Adaptive layout</strong>: each refresh reads
/// <see cref="Console.WindowHeight"/> and <see cref="Console.WindowWidth"/> and
/// computes the row split. The last visible row is always the menu prompt; the
/// rest is split across the dashboard sections by priority. Sections are separated
/// by a blank row for visual breathing room. If the window is very small, low
/// priority sections are dropped without failing. If the user resizes the window
/// live, the dashboard re-adapts on the next refresh.
/// </para>
/// <para>
/// Section priorities, highest to lowest:
/// </para>
/// <list type="number">
///   <item>Menu prompt (1 fixed row, last visible). Painted by
///   <see cref="ConsoleMenu"/>, not the dashboard.</item>
///   <item>Header (3 rows): title + time/mixer + module status.</item>
///   <item>Channel and bus table (minimum 3 rows).</item>
///   <item>Recent events (Warning/Error, max 5 lines).</item>
///   <item>Delay-slot table (3 rows if they fit).</item>
/// </list>
/// <para>
/// <strong>Recent events vs full log</strong>: the dashboard shows only Warning or
/// Error events via <see cref="ShowLogger.GetRecentImportantEvents"/>. Info events
/// (routine AutoLeveler corrections, etc.) do not appear on screen but do go to the
/// log file on disk, so the UI is not flooded with hundreds of messages per minute.
/// </para>
/// <para>
/// <strong>Overlaid submenu</strong>: when the menu opens a submenu, it pauses the
/// dashboard refresh via <see cref="Pause"/> and paints over the dashboard's last
/// rows (the <see cref="SubmenuAreaLines"/> just above the prompt). When the
/// submenu closes, the menu calls <see cref="Resume"/>, which clears the screen and
/// repaints. The menu queries <see cref="MenuPromptRow"/>,
/// <see cref="SubmenuFirstRow"/> and <see cref="SubmenuAreaLines"/> to know where to
/// write.
/// </para>
/// <para>
/// <strong>Concurrency</strong>: console writes are serialized with
/// <see cref="AppOrchestrator.ConsoleLock"/>, which the menu also uses. The refresh
/// thread and the main thread (where the menu lives) compete for the console; the
/// lock guarantees no frame is interleaved with a menu write.
/// </para>
/// </remarks>
public class ConsoleDashboard
{
    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sleep granularity inside the refresh loop, in milliseconds. The loop wakes in
    /// slices this small so <see cref="Stop"/> is honored promptly, while the frame
    /// itself is paced to the one-second clock boundary (see <see cref="RefreshLoop"/>).
    /// </summary>
    private const int REFRESH_SLEEP_TICK_MS = 50;

    /// <summary>Default width if the API cannot read the real one.</summary>
    private const int DEFAULT_WIDTH = 110;

    /// <summary>Default height if the API cannot read the real one.</summary>
    private const int DEFAULT_HEIGHT = 35;

    /// <summary>
    /// Minimum window width below which the dashboard is not drawn; a "too small"
    /// notice is shown instead. The app cannot (portably) stop the user from
    /// shrinking the terminal, so it shows a clear message rather than a broken UI.
    /// </summary>
    private const int MIN_DASHBOARD_WIDTH = 66;

    /// <summary>Minimum window height below which the "too small" notice is shown.</summary>
    private const int MIN_DASHBOARD_HEIGHT = 12;

    /// <summary>Number of rows the header takes (title + time + status).</summary>
    private const int HEADER_LINES = 3;

    /// <summary>Maximum number of events shown in the recent-events section.</summary>
    private const int MAX_LOG_LINES = 5;

    /// <summary>
    /// Age (ms) beyond which the displayed BPM is flagged as stale, i.e. no new
    /// transient has refreshed the tempo recently (the music likely stopped).
    /// </summary>
    private const double BPM_STALE_MS = 6000.0;

    /// <summary>
    /// Rows reserved for the submenu area above the main prompt. Enough for the
    /// largest submenus (header + options + input prompt).
    /// </summary>
    private const int SUBMENU_LINES = 8;

    // ─────────────────────────────────────────────────────────────────────
    // ANSI sequences
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>ASCII escape character (27). Start of every ANSI sequence.</summary>
    private const char ESCAPE = (char)27;

    /// <summary>Escape character as a string, the prefix of every ANSI sequence below.</summary>
    private static readonly string ESC = ESCAPE.ToString();

    /// <summary>Enter the alternate screen buffer (vim/htop mode).</summary>
    private static readonly string ENTER_ALT_SCREEN = ESC + "[?1049h";

    /// <summary>Leave the alternate buffer and restore the normal one.</summary>
    private static readonly string EXIT_ALT_SCREEN = ESC + "[?1049l";

    /// <summary>Hide the cursor.</summary>
    private static readonly string HIDE_CURSOR = ESC + "[?25l";

    /// <summary>Show the cursor.</summary>
    private static readonly string SHOW_CURSOR = ESC + "[?25h";

    /// <summary>Clear the whole screen.</summary>
    private static readonly string CLEAR_SCREEN = ESC + "[2J";

    /// <summary>Move the cursor to position (1,1).</summary>
    private static readonly string CURSOR_HOME = ESC + "[H";

    // ─────────────────────────────────────────────────────────────────────
    // ANSI colors (SGR). Reset clears them; every colored span ends with RESET so
    // color never bleeds into the next cell. Codes are zero-width, so they can be
    // wrapped around already-padded text without changing its on-screen width.
    // ─────────────────────────────────────────────────────────────────────

    private static readonly string COL_RESET = ESC + "[0m";
    private static readonly string COL_TITLE = ESC + "[1;96m";  // bold bright cyan
    private static readonly string COL_GREEN = ESC + "[92m";
    private static readonly string COL_YELLOW = ESC + "[93m";
    private static readonly string COL_RED = ESC + "[91m";
    private static readonly string COL_CYAN = ESC + "[96m";
    private static readonly string COL_GRAY = ESC + "[90m";

    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private AppOrchestrator orchestrator;
    private volatile bool isRunning;
    private volatile bool isPaused;
    private Thread? refreshThread;
    private bool altScreenActive;

    /// <summary>Window width at the last refresh. Used to truncate writes to the width.</summary>
    private int lastWindowWidth;

    /// <summary>
    /// Content of each dashboard row in the last painted frame (index 0 = row 1).
    /// Each new frame is diffed against this and only the rows that actually changed
    /// are rewritten, so steady content like the clock never flickers.
    /// </summary>
    private string[]? lastRows;

    /// <summary>Window width of the last painted frame; a change forces a full repaint.</summary>
    private int lastFrameWidth = -1;

    /// <summary>
    /// The menu legend the dashboard paints at the bottom. Set by <see cref="ConsoleMenu"/>.
    /// The dashboard owns it (rather than the menu painting it directly) so a window
    /// resize, handled by the refresh thread, always repaints it in the right place —
    /// the menu thread is otherwise blocked reading a key and cannot react to a resize.
    /// </summary>
    private volatile string menuPrompt = "";

    /// <summary>First absolute row of the (possibly wrapped) prompt, recomputed each frame.</summary>
    private int lastPromptStartRow = DEFAULT_HEIGHT;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the dashboard bound to the orchestrator. Does not start the refresh
    /// thread until <see cref="Start"/> is called.
    /// </summary>
    public ConsoleDashboard(AppOrchestrator orchestrator)
    {
        this.orchestrator = orchestrator;
        this.isRunning = false;
        this.isPaused = false;
        this.refreshThread = null;
        this.altScreenActive = false;
        this.lastWindowWidth = DEFAULT_WIDTH;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties (dynamic layout for the menu)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// First absolute row of the (possibly multi-line) menu legend at the bottom.
    /// Recomputed on every refresh from the window height and the wrapped legend.
    /// </summary>
    public int MenuPromptRow
    {
        get { return Math.Max(1, lastPromptStartRow); }
    }

    /// <summary>
    /// First absolute row of the area reserved for submenus. Just above the menu
    /// legend, leaving room for <see cref="SUBMENU_LINES"/> rows.
    /// </summary>
    public int SubmenuFirstRow
    {
        get { return Math.Max(1, lastPromptStartRow - SUBMENU_LINES); }
    }

    /// <summary>
    /// Sets the menu legend painted at the bottom of the screen. The dashboard wraps
    /// it to the window width and paints it as part of every frame, so it survives a
    /// resize. Repaints immediately when the dashboard is running and not paused.
    /// </summary>
    public void SetMenuPrompt(string text)
    {
        menuPrompt = text ?? "";
        if (isRunning && !isPaused)
        {
            DrawAll();
        }
    }

    /// <summary>
    /// Number of rows available for submenu content. Constant.
    /// </summary>
    public int SubmenuAreaLines
    {
        get { return SUBMENU_LINES; }
    }

    /// <summary>Current window width in columns.</summary>
    public int CurrentWidth
    {
        get { return lastWindowWidth; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the dashboard. Enables ANSI on Windows, enters the alternate buffer,
    /// hides the cursor, paints the first frame and starts the refresh thread.
    /// </summary>
    public void Start()
    {
        if (isRunning)
        {
            return;
        }

        EnableAnsi();

        Console.Write(ENTER_ALT_SCREEN);
        Console.Write(HIDE_CURSOR);
        Console.Write(CLEAR_SCREEN);
        Console.Write(CURSOR_HOME);
        altScreenActive = true;

        DrawAll();

        isRunning = true;
        refreshThread = new Thread(RefreshLoop);
        refreshThread.IsBackground = true;
        refreshThread.Name = "ConsoleDashboard-Refresh";
        refreshThread.Start();
    }

    /// <summary>
    /// Stops the dashboard. Stops the refresh thread, shows the cursor again and
    /// leaves the alternate buffer (restoring the console as it was before start).
    /// </summary>
    public void Stop()
    {
        if (!isRunning)
        {
            return;
        }

        isRunning = false;

        if (refreshThread != null && refreshThread.IsAlive)
        {
            refreshThread.Join(1000);
            refreshThread = null;
        }

        if (altScreenActive)
        {
            Console.Write(SHOW_CURSOR);
            Console.Write(EXIT_ALT_SCREEN);
            altScreenActive = false;
        }
    }

    /// <summary>
    /// Pauses the dashboard refresh. The refresh thread stays alive but paints
    /// nothing. Used by the menu while a submenu is shown so it does not interfere
    /// with the submenu's writes.
    /// </summary>
    public void Pause()
    {
        isPaused = true;
    }

    /// <summary>
    /// Resumes the dashboard refresh. Clears the whole screen and immediately
    /// repaints a frame so the transition is clean (no leftovers from the submenu
    /// that just closed).
    /// </summary>
    public void Resume()
    {
        isPaused = false;
        ClearScreen();
        DrawAll();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Primitive painting exposed to the menu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes text at an absolute row, column 1, and clears the rest of the line
    /// with <c>ESC [K</c>. Truncates the text to the current console width to avoid
    /// involuntary wrapping. Atomic thanks to the shared console lock.
    /// </summary>
    /// <param name="row">Absolute row (1-based).</param>
    /// <param name="text">Text to write.</param>
    public void WriteToRow(int row, string text)
    {
        lock (orchestrator.ConsoleLock)
        {
            int maxLen = Math.Max(1, lastWindowWidth - 1);
            string truncated = text.Length > maxLen ? text.Substring(0, maxLen) : text;

            StringBuilder sb = new StringBuilder();
            sb.Append(ESC).Append('[').Append(row).Append(";1H");
            sb.Append(truncated);
            sb.Append(ESC).Append("[K");
            Console.Write(sb.ToString());
        }
    }

    /// <summary>
    /// Clears a row completely (fills it with spaces).
    /// </summary>
    public void ClearRow(int row)
    {
        lock (orchestrator.ConsoleLock)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ESC).Append('[').Append(row).Append(";1H");
            sb.Append(ESC).Append("[2K");
            Console.Write(sb.ToString());
        }
    }

    /// <summary>
    /// Moves the cursor to an absolute position. Used so the menu's
    /// <see cref="Console.ReadLine"/> or <see cref="Console.ReadKey"/> echo lands in
    /// the right place.
    /// </summary>
    /// <param name="row">Absolute row (1-based).</param>
    /// <param name="column">Absolute column (1-based).</param>
    public void MoveCursorTo(int row, int column)
    {
        lock (orchestrator.ConsoleLock)
        {
            Console.Write($"{ESC}[{row};{column}H");
        }
    }

    /// <summary>Shows the cursor (useful when asking for text input).</summary>
    public void ShowCursor()
    {
        Console.Write(SHOW_CURSOR);
    }

    /// <summary>Hides the cursor (the dashboard's default state).</summary>
    public void HideCursor()
    {
        Console.Write(HIDE_CURSOR);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Refresh loop
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Main loop of the refresh thread. Repaints once per second, paced to the
    /// system-clock second boundary so the clock advances exactly once per second
    /// (no doubled or skipped seconds) while the thread stays idle between frames
    /// instead of busy-polling. If the window size changed since the last frame it
    /// clears the screen first to erase leftovers from the previous layout.
    /// </summary>
    private void RefreshLoop()
    {
        int lastH = -1;
        int lastW = -1;

        while (isRunning)
        {
            if (!isPaused)
            {
                try
                {
                    (int currentW, int currentH) = GetConsoleSize();

                    if (currentH != lastH || currentW != lastW)
                    {
                        ClearScreen();
                        lastH = currentH;
                        lastW = currentW;
                    }

                    DrawAll();
                }
                catch
                {
                    // Any paint error is ignored; the next cycle retries with the
                    // updated size.
                }
            }

            SleepUntilNextSecondOrResize(lastH, lastW);
        }
    }

    /// <summary>
    /// Sleeps until just past the next whole-second boundary of the system clock (so
    /// the clock ticks cleanly once per second and the thread stays idle between
    /// frames), but returns early if the window is resized so the layout reflows
    /// within ~50 ms instead of up to a second later. Returns promptly when paused or
    /// stopped.
    /// </summary>
    private void SleepUntilNextSecondOrResize(int knownH, int knownW)
    {
        // A small margin past the boundary guarantees DateTime.Now has rolled over
        // to the new second by the time the next frame reads it.
        int remaining = 1000 - DateTime.Now.Millisecond + 15;

        while (remaining > 0 && isRunning)
        {
            int tick = Math.Min(REFRESH_SLEEP_TICK_MS, remaining);
            Thread.Sleep(tick);
            remaining -= tick;

            if (isPaused)
            {
                return;
            }
            (int w, int h) = GetConsoleSize();
            if (h != knownH || w != knownW)
            {
                return; // size changed: repaint promptly
            }
        }
    }

    /// <summary>Clears the whole screen and returns the cursor to the origin.</summary>
    private void ClearScreen()
    {
        lock (orchestrator.ConsoleLock)
        {
            Console.Write(CLEAR_SCREEN);
            Console.Write(CURSOR_HOME);
            // The physical screen is now blank, so the diff cache no longer matches
            // what is shown: force a full repaint on the next frame.
            lastRows = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dashboard painting (adaptive)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Paints a centered "terminal too small" notice instead of the dashboard, when
    /// the window is below the usable minimum. Sized to the real terminal so it does
    /// not wrap, and updated live (it shows the current size) as the user resizes.
    /// </summary>
    private void PaintTooSmall(int realWidth, int realHeight)
    {
        int h = Math.Max(1, realHeight);
        int w = Math.Max(1, realWidth);

        // Keep the menu coordinates sane while the dashboard is not drawn.
        lastPromptStartRow = h;
        lastWindowWidth = w;

        string[] rows = new string[h];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = "";
        }

        (string text, string color)[] message =
        {
            ("M32 Live Console Tool", COL_TITLE),
            ("", ""),
            ("Terminal too small", COL_YELLOW),
            ($"Resize to at least {MIN_DASHBOARD_WIDTH} x {MIN_DASHBOARD_HEIGHT}", ""),
            ($"(now {realWidth} x {realHeight})", COL_GRAY),
        };

        int startRow = Math.Max(1, (h - message.Length) / 2 + 1);
        for (int i = 0; i < message.Length; i++)
        {
            int row = startRow + i;
            if (row > h)
            {
                break;
            }
            int pad = Math.Max(0, (w - 1 - message[i].text.Length) / 2);
            string centered = new string(' ', pad) + message[i].text;
            if (message[i].color.Length > 0)
            {
                SetRowRaw(rows, row, message[i].color + PadPlain(centered, w) + COL_RESET);
            }
            else
            {
                SetRow(rows, row, centered, w);
            }
        }

        EmitFrame(rows, w);
    }

    /// <summary>
    /// Paints a full dashboard frame, distributing the available rows across the
    /// sections by priority order and available space, with the wrapped menu legend
    /// reserved at the bottom. Sections are separated by a blank row and dropped if
    /// they do not fit. Only rows that changed since the previous frame are rewritten.
    /// </summary>
    private void DrawAll()
    {
        lock (orchestrator.ConsoleLock)
        {
            (int width, int height) = GetConsoleSize();

            // If the terminal is too small to show a usable dashboard, paint a clear
            // notice instead of a broken layout. The app cannot portably prevent the
            // user from shrinking the window, so this is the robust alternative.
            if (width < MIN_DASHBOARD_WIDTH || height < MIN_DASHBOARD_HEIGHT)
            {
                PaintTooSmall(width, height);
                return;
            }

            lastWindowWidth = width;

            // The menu legend lives at the bottom, wrapped to the width across as many
            // rows as it needs, so it is never truncated. The dashboard sections fill
            // the rows above it. Painting the legend here (not in the menu) is what
            // makes it survive a resize.
            List<string> promptLines = WrapText(menuPrompt, Math.Max(1, width - 1));
            int promptHeight = Math.Min(Math.Max(1, promptLines.Count), Math.Max(1, height - 1));
            int promptStartRow = Math.Max(1, height - promptHeight + 1);
            lastPromptStartRow = promptStartRow;
            int dashboardLastRow = promptStartRow - 1;

            // Build the content of every screen row. EmitFrame then writes only the
            // rows that changed since the previous frame, which is what keeps the
            // screen — and the clock — from flickering: an unchanged row is never
            // redrawn.
            string[] rows = new string[Math.Max(1, height)];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = "";
            }

            int available = dashboardLastRow;
            int currentRow = 1;

            // Header (3 fixed rows).
            if (available >= HEADER_LINES)
            {
                PaintHeader(rows, currentRow, width);
                currentRow += HEADER_LINES;
                available -= HEADER_LINES;
            }

            // Blank row.
            if (available >= 1)
            {
                SetRow(rows, currentRow, "", width);
                currentRow += 1;
                available -= 1;
            }

            // Channel table (minimum header + 1 row = 2; aims for more).
            int channelsRows = 0;
            int devicesCount = orchestrator.MeterReader.Devices.Count;
            int channelsIdeal = 2 + devicesCount;

            if (available >= 2)
            {
                channelsRows = Math.Min(channelsIdeal, available);
                // Reserve room for the log and slots if they fit; trim channels if
                // it overflows too much.
                int reserveForRest = 0;
                if (available - channelsRows >= 1 + 1 + MAX_LOG_LINES)
                {
                    // Events (title + 1 minimum) plus the preceding blank row.
                    reserveForRest += 1 + 1 + Math.Min(MAX_LOG_LINES, 3);
                }
                if (available - channelsRows - reserveForRest >= 1 + 1 + 3)
                {
                    // Slots (title + header + 1) plus the preceding blank row.
                    reserveForRest += 1 + 1 + 3;
                }
                channelsRows = Math.Min(channelsRows, available - reserveForRest);
                channelsRows = Math.Max(channelsRows, 2);

                PaintChannelsTable(rows, currentRow, width, channelsRows);
                currentRow += channelsRows;
                available -= channelsRows;
            }

            // Blank row before events.
            if (available >= 1 + 2)
            {
                SetRow(rows, currentRow, "", width);
                currentRow += 1;
                available -= 1;

                // Recent events.
                int eventsRows = Math.Min(MAX_LOG_LINES + 1, available);  // +1 for the title
                PaintRecentEvents(rows, currentRow, width, eventsRows);
                currentRow += eventsRows;
                available -= eventsRows;
            }

            // Blank row before slots.
            if (available >= 1 + 3)
            {
                SetRow(rows, currentRow, "", width);
                currentRow += 1;
                available -= 1;

                // Slots.
                int slotsRows = Math.Min(5, available);
                PaintSlotsTable(rows, currentRow, width, slotsRows);
                currentRow += slotsRows;
                available -= slotsRows;
            }

            // Fill the rest with blank rows so the legend sits at the bottom.
            while (currentRow <= dashboardLastRow)
            {
                SetRow(rows, currentRow, "", width);
                currentRow++;
            }

            // Paint the wrapped menu legend across the reserved bottom rows, with the
            // bracketed shortcut keys highlighted.
            for (int i = 0; i < promptHeight; i++)
            {
                string lineText = i < promptLines.Count ? promptLines[i] : "";
                SetRowRaw(rows, promptStartRow + i, ColorizeKeys(PadPlain(lineText, width)));
            }

            EmitFrame(rows, width);
        }
    }

    /// <summary>
    /// Highlights bracketed shortcut keys (e.g. <c>[A]</c>, <c>[0]</c>) in a legend
    /// line by wrapping each in color. The color codes are zero-width, so the line's
    /// on-screen width is unchanged and the padding stays correct.
    /// </summary>
    private static string ColorizeKeys(string line)
    {
        StringBuilder sb = new StringBuilder(line.Length + 16);
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '[' && i + 2 < line.Length && line[i + 2] == ']')
            {
                sb.Append(COL_CYAN).Append('[').Append(line[i + 1]).Append(']').Append(COL_RESET);
                i += 2;
            }
            else
            {
                sb.Append(line[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Word-wraps text to lines no wider than <paramref name="maxWidth"/>, breaking on
    /// spaces so each menu item stays intact. Always returns at least one line.
    /// </summary>
    private static List<string> WrapText(string text, int maxWidth)
    {
        List<string> lines = new List<string>();
        if (maxWidth < 1)
        {
            maxWidth = 1;
        }

        StringBuilder current = new StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= maxWidth)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }
        if (lines.Count == 0)
        {
            lines.Add("");
        }
        return lines;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dashboard sections
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Paints the header: title, current time + mixer IP, and a summary of the
    /// automatic modules' status with the current BPM.
    /// </summary>
    private void PaintHeader(string[] rows, int startRow, int width)
    {
        string title = "M32 Live Console Tool";
        if (orchestrator.SimulationMode)
        {
            title += "   [SIMULATION]";
        }
        SetRowRaw(rows, startRow, COL_TITLE + PadPlain(title, width) + COL_RESET);

        string time = DateTime.Now.ToString("HH:mm:ss");
        string uptime = FormatUptime(orchestrator.SessionUptime);
        string mixerIp = orchestrator.SimulationMode
            ? "127.0.0.1 (simulated)" : orchestrator.Config.MixerIp;
        bool receiving = orchestrator.MeterReader.IsReceivingMeters;
        string link = receiving ? "receiving meters" : "NO SIGNAL";
        string line2 = PadPlain(
            $"  Time: {time}   Up: {uptime}   Mixer: {mixerIp}  [{link}]", width);
        // Color only the link token; codes are zero-width so the padding stays correct.
        line2 = receiving
            ? line2.Replace("receiving meters", COL_GREEN + "receiving meters" + COL_RESET)
            : line2.Replace("NO SIGNAL", COL_RED + "NO SIGNAL" + COL_RESET);
        SetRowRaw(rows, startRow + 1, line2);

        string al = orchestrator.AutoLeveler.IsEnabled ? "[ON] " : "[OFF]";
        string ac = orchestrator.AntiClipper.IsEnabled ? "[ON] " : "[OFF]";
        string bd = orchestrator.BpmDetector.IsEnabled ? "[ON] " : "[OFF]";
        string bpmText;
        bool bpmStale = false;
        if (orchestrator.BpmDetector.HasEverBeenValid)
        {
            bpmText = $"BPM: {orchestrator.BpmDetector.CurrentBpm:F1}";
            // Flag a stale reading: if no new transient has updated the tempo for a
            // while, the music likely stopped and the value shown is no longer live.
            double ageMs = orchestrator.BpmDetector.LastBpmAgeMs;
            if (ageMs > BPM_STALE_MS)
            {
                bpmText += $" (stale {(int)(ageMs / 1000)}s)";
                bpmStale = true;
            }
        }
        else
        {
            bpmText = "BPM: ---";
        }

        string line3 = PadPlain(
            $"  AutoLeveler {al}  Anti-Clip {ac}  BpmDetector {bd}   {bpmText}", width);
        line3 = line3
            .Replace("[ON] ", COL_GREEN + "[ON] " + COL_RESET)
            .Replace("[OFF]", COL_GRAY + "[OFF]" + COL_RESET);
        if (bpmStale)
        {
            line3 = line3.Replace(bpmText, COL_YELLOW + bpmText + COL_RESET);
        }
        SetRowRaw(rows, startRow + 2, line3);
    }

    /// <summary>Formats a session uptime as HH:MM:SS, allowing the hours to exceed 24.</summary>
    private static string FormatUptime(TimeSpan up)
    {
        if (up < TimeSpan.Zero)
        {
            up = TimeSpan.Zero;
        }
        return $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
    }

    /// <summary>
    /// Paints the table of watched channels and buses with their live data (average
    /// level, target, preamp, mute). If there are more devices than available rows,
    /// it shows the ones that fit and a "... and N more" indicator at the end.
    /// </summary>
    /// <param name="startRow">First absolute row where the table starts.</param>
    /// <param name="width">Available width.</param>
    /// <param name="totalRows">Total rows assigned to the table, header included.</param>
    private void PaintChannelsTable(string[] rows, int startRow, int width, int totalRows)
    {
        SetRow(rows, startRow, "Watched channels and buses:  (* = BPM source)", width);
        SetRow(rows, startRow + 1, ChannelsHeaderLine(), width);

        IReadOnlyList<AudioDevice> devices = orchestrator.MeterReader.Devices;
        int dataRows = totalRows - 2;
        if (dataRows <= 0)
        {
            return;
        }

        // If the devices do not all fit, reserve the last data row for an
        // "... and N more" indicator so no device is silently hidden under it.
        bool overflow = devices.Count > dataRows;
        int deviceCountShown = overflow ? dataRows - 1 : devices.Count;

        for (int i = 0; i < deviceCountShown; i++)
        {
            AudioDevice device = devices[i];
            double avg = orchestrator.MeterReader.GetMovingAverage(device);
            string text = FormatChannelRow(device, avg);
            string composed = ComposeLevelRow(text, avg, device.TargetLevelDb, RowColorForDevice(device), width);
            SetRowRaw(rows, startRow + 2 + i, composed);
        }

        if (overflow)
        {
            int hiddenCount = devices.Count - deviceCountShown;
            SetRow(rows, startRow + 2 + deviceCountShown,
                $"   ... and {hiddenCount} more (make the window taller to see them)", width);
        }

        // Fill any remaining rows with blanks.
        int firstBlank = overflow ? deviceCountShown + 1 : deviceCountShown;
        for (int i = firstBlank; i < dataRows; i++)
        {
            SetRow(rows, startRow + 2 + i, "", width);
        }
    }

    /// <summary>
    /// Header line for the channel table, built with the same field widths as
    /// <see cref="FormatChannelRow"/> so the columns line up exactly.
    /// </summary>
    private static string ChannelsHeaderLine()
    {
        return $" {"#",2} {"Typ",-3} {"Name",-14} {"LvlAvg",6} {"Target",6} {"Preamp",6} {"Src",-4} {"M"}  Level";
    }

    /// <summary>
    /// Formats the fixed-width text columns of a channel-table row (without the level
    /// bar, which is appended by <see cref="ComposeLevelRow"/>). Truncates the name to
    /// 14 characters and hides the preamp for buses (which have none). A leading
    /// <c>*</c> marks the channel the BpmDetector reads its tempo from. Takes the
    /// already-computed moving average to avoid sampling the history twice.
    /// </summary>
    private string FormatChannelRow(AudioDevice device, double avg)
    {
        string type = device is MixerChannel ? "Ch" : "Bus";
        string name = TruncatePad(device.Name, 14);
        string lvlAvg = avg <= -89.0 ? "---" : $"{avg:F1}";
        string target = $"{device.TargetLevelDb:F1}";

        string preamp;
        string source;
        if (device is MixerChannel ch)
        {
            preamp = $"{ch.PreampGain:F1}";
            // Where the gain is controlled: a head amp (local XLR or AES50 stage
            // box) or the channel's digital trim.
            source = ch.HeadAmpIndex >= 0 ? $"HA{ch.HeadAmpIndex}" : "trim";
        }
        else
        {
            preamp = "---";
            source = "---";
        }

        string mute = device.IsMuted ? "M" : " ";

        // Mark the channel the BpmDetector reads its tempo from.
        string marker = device is MixerChannel bpmCh
            && ReferenceEquals(bpmCh, orchestrator.BpmDetector.TargetChannel)
            ? "*" : " ";

        return $"{marker}{device.OscNumber,2} {type,-3} {name} {lvlAvg,6} {target,6} {preamp,6} {source,-4} {mute}";
    }

    /// <summary>
    /// Paints the table of configured delay slots. If a slot is automatic, it shows
    /// its rhythmic figure and the time in ms computed with the current BPM. If it
    /// is manual, it shows the fixed milliseconds.
    /// </summary>
    private void PaintSlotsTable(string[] rows, int startRow, int width, int totalRows)
    {
        SetRow(rows, startRow, "Configured delay slots:", width);
        SetRow(rows, startRow + 1, "  Slot  Effect              Mode        Figure / ms", width);

        IReadOnlyDictionary<int, DelaySlotConfig> slots = orchestrator.DelayController.DelaySlots;
        IReadOnlyDictionary<int, DetectedEffect> detected = orchestrator.DelayController.DetectedEffects;
        int dataRows = totalRows - 2;
        if (dataRows <= 0)
        {
            return;
        }

        if (slots.Count == 0)
        {
            SetRow(rows, startRow + 2, "  (no slots configured)", width);
            for (int i = 1; i < dataRows; i++)
            {
                SetRow(rows, startRow + 2 + i, "", width);
            }
            return;
        }

        List<KeyValuePair<int, DelaySlotConfig>> ordered =
            new List<KeyValuePair<int, DelaySlotConfig>>(slots);
        ordered.Sort((a, b) => a.Key.CompareTo(b.Key));

        // If the slots do not all fit, reserve the last row for an "... and N more"
        // indicator so none is silently hidden.
        bool overflow = ordered.Count > dataRows;
        int slotsShown = overflow ? dataRows - 1 : ordered.Count;

        for (int i = 0; i < slotsShown; i++)
        {
            int slot = ordered[i].Key;
            DelaySlotConfig config = ordered[i].Value;

            string effect = detected.TryGetValue(slot, out DetectedEffect? fx)
                ? TruncatePad(fx.Name, 18)
                : TruncatePad("(not detected)", 18);

            string mode = config.IsAutomatic ? "Automatic  " : "Manual     ";
            string detail;
            if (config.IsAutomatic)
            {
                double currentBpm = orchestrator.BpmDetector.HasEverBeenValid
                    ? orchestrator.BpmDetector.CurrentBpm : 0.0;
                // Show the value the console actually receives: a long figure at a
                // slow tempo can exceed the 3000 ms the M32 caps the time at.
                double ms = DelayEffects.ClampTimeMs(config.GetDelayMs(currentBpm));
                string msText = currentBpm > 0 ? $"{ms,5:F0}ms" : "   ---";
                detail = $"{config.Beat,-16} {msText}";
            }
            else
            {
                detail = $"{"Manual",-16} {DelayEffects.ClampTimeMs(config.ManualMs),5:F0}ms";
            }

            SetRow(rows, startRow + 2 + i, $"   {slot}    {effect}  {mode} {detail}", width);
        }

        if (overflow)
        {
            int hiddenCount = ordered.Count - slotsShown;
            SetRow(rows, startRow + 2 + slotsShown,
                $"   ... and {hiddenCount} more (make the window taller to see them)", width);
        }

        int firstBlank = overflow ? slotsShown + 1 : slotsShown;
        for (int i = firstBlank; i < dataRows; i++)
        {
            SetRow(rows, startRow + 2 + i, "", width);
        }
    }

    /// <summary>
    /// Paints the "Recent events" section with the latest anomalies (Warning/Error)
    /// recorded by the logger. Routine events (Info) do not appear here so the
    /// screen is not flooded; they stay in the log file on disk. If there are no
    /// important events, it shows a placeholder message.
    /// </summary>
    private void PaintRecentEvents(string[] rows, int startRow, int width, int totalRows)
    {
        SetRow(rows, startRow, "Recent events:", width);

        int dataRows = totalRows - 1;
        if (dataRows <= 0)
        {
            return;
        }

        List<LogEntry> recent = orchestrator.Logger.GetRecentImportantEvents(dataRows);

        if (recent.Count == 0)
        {
            SetRow(rows, startRow + 1, "  (no events)", width);
            for (int i = 1; i < dataRows; i++)
            {
                SetRow(rows, startRow + 1 + i, "", width);
            }
            return;
        }

        for (int i = 0; i < dataRows; i++)
        {
            if (i < recent.Count)
            {
                LogEntry entry = recent[i];
                string sev = entry.Severity == Severity.Error ? "[ERR]" : "[WRN]";
                string line = $"  [{entry.Timestamp:HH:mm:ss}] {sev} {entry.Message}";
                string color = entry.Severity == Severity.Error ? COL_RED : COL_YELLOW;
                SetRowRaw(rows, startRow + 1 + i, color + PadPlain(line, width) + COL_RESET);
            }
            else
            {
                SetRow(rows, startRow + 1 + i, "", width);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Internal primitive painting
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores one row's content (truncated or space-padded to the width) at its
    /// absolute position in the frame buffer. The actual console write is deferred
    /// to <see cref="EmitFrame"/>, which writes only the rows that changed.
    /// </summary>
    private static void SetRow(string[] rows, int row, string text, int width)
    {
        int index = row - 1;
        if (index < 0 || index >= rows.Length)
        {
            return;
        }

        rows[index] = PadPlain(text, width);
    }

    /// <summary>Truncates or space-pads plain text to the usable width (width - 1).</summary>
    private static string PadPlain(string text, int width)
    {
        int targetWidth = Math.Max(1, width - 1);
        if (text.Length > targetWidth)
        {
            return text.Substring(0, targetWidth);
        }
        return text + new string(' ', targetWidth - text.Length);
    }

    /// <summary>
    /// Stores a row's content verbatim (already sized / already carrying ANSI codes),
    /// bypassing the plain-text padding in <see cref="SetRow"/>.
    /// </summary>
    private static void SetRowRaw(string[] rows, int row, string content)
    {
        int index = row - 1;
        if (index < 0 || index >= rows.Length)
        {
            return;
        }
        rows[index] = content;
    }

    /// <summary>
    /// Builds a fixed-width level meter bar for a dB value over a -60..+6 dB scale,
    /// with a <c>|</c> marker at the target level. Filled cells are <c>#</c>.
    /// </summary>
    private static string LevelBar(double db, double targetDb, int width)
    {
        if (width <= 0)
        {
            return "";
        }

        const double min = -60.0;
        const double max = 6.0;

        int filled = (int)Math.Round(Math.Clamp((db - min) / (max - min), 0.0, 1.0) * width);
        int targetPos = (int)Math.Round(Math.Clamp((targetDb - min) / (max - min), 0.0, 1.0) * (width - 1));

        StringBuilder sb = new StringBuilder(width);
        for (int i = 0; i < width; i++)
        {
            if (i == targetPos)
            {
                sb.Append('|');
            }
            else if (i < filled)
            {
                sb.Append('#');
            }
            else
            {
                sb.Append(' ');
            }
        }
        return sb.ToString();
    }

    /// <summary>Bar color by how close the level is to clipping: green / amber / red.</summary>
    private static string BarColor(double db)
    {
        if (db >= -3.0)
        {
            return COL_RED;
        }
        if (db >= -6.0)
        {
            return COL_YELLOW;
        }
        return COL_GREEN;
    }

    /// <summary>
    /// Row color by what the tool is doing to a device: red if the AntiClipper just
    /// reduced it, amber under the engineer's manual override, cyan while the leveler
    /// is actively correcting, default when idle.
    /// </summary>
    private string RowColorForDevice(AudioDevice device)
    {
        if (device is MixerChannel ch
            && orchestrator.AntiClipper.IsEnabled
            && orchestrator.AntiClipper.WasRecentlyReduced(ch))
        {
            return COL_RED;
        }

        if (orchestrator.AutoLeveler.IsEnabled)
        {
            LevelingState state = orchestrator.AutoLeveler.GetDeviceState(device);
            if (state == LevelingState.ManualOverride)
            {
                return COL_YELLOW;
            }
            if (state == LevelingState.Correcting)
            {
                return COL_CYAN;
            }
        }

        return "";
    }

    /// <summary>
    /// Composes a channel row: the text columns in the row's state color, followed by
    /// a level meter bar colored by proximity to clipping. Falls back to plain padding
    /// when the window is too narrow for a bar. The visible width is always width - 1.
    /// </summary>
    private string ComposeLevelRow(string text, double levelDb, double targetDb, string rowColor, int width)
    {
        int targetWidth = Math.Max(1, width - 1);

        if (text.Length >= targetWidth)
        {
            string clipped = text.Substring(0, targetWidth);
            return rowColor.Length > 0 ? rowColor + clipped + COL_RESET : clipped;
        }

        int remaining = targetWidth - text.Length;
        string coloredText = rowColor.Length > 0 ? rowColor + text + COL_RESET : text;

        // Need " [bar]" -> 3 fixed chars (space + brackets) plus at least a few cells.
        if (remaining >= 8)
        {
            int inner = remaining - 3;
            string bar = LevelBar(levelDb, targetDb, inner);
            return coloredText + " [" + BarColor(levelDb) + bar + COL_RESET + "]";
        }

        return coloredText + new string(' ', remaining);
    }

    /// <summary>
    /// Writes only the rows that changed since the previous frame, each prefixed
    /// with its absolute-position ANSI sequence, in a single
    /// <see cref="Console.Write"/>. Unchanged rows are left on screen untouched, so
    /// steady content like the clock does not flicker. A full repaint happens when
    /// the cache was invalidated (first frame, resize, submenu close) or the row
    /// count changed.
    /// </summary>
    private void EmitFrame(string[] rows, int width)
    {
        bool full = lastRows == null || lastFrameWidth != width || lastRows.Length != rows.Length;

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < rows.Length; i++)
        {
            if (full || lastRows![i] != rows[i])
            {
                sb.Append(ESC).Append('[').Append(i + 1).Append(";1H").Append(rows[i]);
            }
        }

        if (sb.Length > 0)
        {
            // Re-assert the hidden cursor before repositioning it across rows, so it
            // never flashes around the screen on terminals that reveal it on write.
            Console.Write(HIDE_CURSOR + sb.ToString());
        }

        lastRows = rows;
        lastFrameWidth = width;
    }

    /// <summary>
    /// Reads the real terminal size, falling back to the defaults if the API fails
    /// (e.g. output redirected). The values are NOT clamped: callers compare them
    /// against <see cref="MIN_DASHBOARD_WIDTH"/>/<see cref="MIN_DASHBOARD_HEIGHT"/> to
    /// decide whether to draw the dashboard or the "too small" notice, so a genuinely
    /// tiny terminal must be reported as tiny.
    /// </summary>
    private (int width, int height) GetConsoleSize()
    {
        try
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }
        catch
        {
            return (DEFAULT_WIDTH, DEFAULT_HEIGHT);
        }
    }

    /// <summary>
    /// Truncates or space-pads a string to the given width. Used to align names in
    /// the channel table's columns.
    /// </summary>
    private string TruncatePad(string s, int width)
    {
        if (s.Length >= width)
        {
            return s.Substring(0, width);
        }
        return s + new string(' ', width - s.Length);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Enabling ANSI on Windows
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables virtual-terminal sequence processing on the Windows console. Without
    /// it, ANSI sequences are printed literally instead of interpreted. On macOS and
    /// Linux consoles support ANSI natively, so this returns <c>true</c> immediately.
    /// </summary>
    /// <returns><c>true</c> if enabling succeeded or was not needed.</returns>
    private bool EnableAnsi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            IntPtr stdOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (stdOut == IntPtr.Zero || stdOut == new IntPtr(-1))
            {
                return false;
            }

            if (!GetConsoleMode(stdOut, out uint mode))
            {
                return false;
            }

            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;

            if (!SetConsoleMode(stdOut, mode))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Identifier of the standard output handle.</summary>
    private const int STD_OUTPUT_HANDLE = -11;

    /// <summary><c>SetConsoleMode</c> flag that enables ANSI interpretation.</summary>
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    /// <summary>P/Invoke to the native Windows API to get a console handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    /// <summary>P/Invoke to the native Windows API to read the console's current mode.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    /// <summary>P/Invoke to the native Windows API to change the console's mode.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
