using System.Text;
using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Modules;

namespace M32LiveConsoleTool.Ui;

/// <summary>
/// Interactive program menu. Lives on the main thread and blocks in
/// <see cref="Run"/> until the user exits with the Q key. Each key dispatches to a
/// submenu or an operation on the <see cref="AppOrchestrator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Coordinates</strong>: the menu uses the
/// <see cref="ConsoleDashboard"/>'s adaptive coordinate system. The main prompt is
/// painted at <see cref="ConsoleDashboard.MenuPromptRow"/> (the last visible row)
/// and submenus on the rows just above (<see cref="ConsoleDashboard.SubmenuFirstRow"/>).
/// The menu makes no assumptions about the console size: it asks the dashboard
/// where to paint at any moment.
/// </para>
/// <para>
/// <strong>Submenu pattern</strong>:
/// </para>
/// <list type="number">
///   <item>Pause the dashboard (it stops refreshing).</item>
///   <item>Clear the submenu area and the main prompt.</item>
///   <item>Paint all submenu lines with <see cref="PaintSubmenu"/>.</item>
///   <item>Read a key (or a line of text) and act.</item>
///   <item>Clear the submenu area.</item>
///   <item>Resume the dashboard, which repaints the whole frame clean.</item>
/// </list>
/// <para>
/// <strong>Universal cancel with Esc</strong>: every input flow accepts Esc to
/// abandon the operation with no changes. The helpers <see cref="AskString"/>,
/// <see cref="AskInt"/> and <see cref="AskDouble"/> return <c>null</c> when the user
/// presses Esc, and the caller simply abandons the flow with a <c>return</c>.
/// </para>
/// </remarks>
public class ConsoleMenu
{
    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Main-menu key legend. Each item is a single space-free token so the dashboard
    /// can word-wrap it cleanly across as many bottom rows as the width needs (no item
    /// is ever cut). The bracketed letter is the key handled in <see cref="HandleMainKey"/>.
    /// </summary>
    private const string MainMenuLegend =
        "[A]uto-level Anti-[C]lip [B]PM [D]elay [X]clear-slot [F]x-refresh [T]ap " +
        "[R]ecalibrate [N]channels [P]references [S]ave [L]oad [0]release-all [F12]PANIC [Q]uit";

    private AppOrchestrator orchestrator;
    private ConsoleDashboard dashboard;
    private bool shouldExit;
    private readonly OperatorInput input;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the menu bound to the orchestrator and the dashboard. The dashboard
    /// must already be started (with <see cref="ConsoleDashboard.Start"/>) before
    /// calling <see cref="Run"/>.
    /// </summary>
    public ConsoleMenu(AppOrchestrator orchestrator, ConsoleDashboard dashboard)
    {
        this.orchestrator = orchestrator;
        this.dashboard = dashboard;
        this.shouldExit = false;
        input = new OperatorInput(orchestrator.ReleaseAll);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Main loop
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Main menu loop. Blocks the calling thread (normally the program's main
    /// thread) until the user confirms exit with the Q key.
    /// </summary>
    public void Run()
    {
        while (!shouldExit)
        {
            ShowMainPrompt();
            ConsoleKeyInfo key = input.ReadKey();
            HandleMainKey(key);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Main menu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Paints the main prompt on the row reserved by the dashboard.
    /// </summary>
    private void ShowMainPrompt()
    {
        // The dashboard owns the legend now: it wraps it to the width and repaints it
        // every frame, so it survives a window resize (the menu thread is blocked in
        // ReadKey and could not repaint it itself).
        dashboard.SetMenuPrompt(MainMenuLegend);
    }

    /// <summary>
    /// Dispatches the pressed key to the matching submenu or action. Unrecognized
    /// keys are ignored silently.
    /// </summary>
    private void HandleMainKey(ConsoleKeyInfo key)
    {
        switch (char.ToUpperInvariant(key.KeyChar))
        {
            case 'A': HandleSubmenuAutoLeveler(); break;
            case 'C': HandleSubmenuAntiClipper(); break;
            case 'B': HandleSubmenuBpmDetector(); break;
            case 'D': HandleConfigureDelaySlot(); break;
            case 'X': HandleClearDelaySlot(); break;
            case 'F': HandleRefreshEffects(); break;
            case 'T': HandleTapTempo(); break;
            case 'R': HandleRecalibrate(); break;
            case 'N': HandleSubmenuChannels(); break;
            case 'P': HandleSubmenuPreferences(); break;
            case 'S': HandleSaveConfig(); break;
            case 'L': HandleLoadConfig(); break;
            case '0': HandlePanic(); break;
            case 'Q': HandleQuit(); break;
            default: break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // AutoLeveler submenu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AutoLeveler submenu: toggle on/off, choose the slew speed (presets or a custom
    /// up/down pair), and edit the numeric parameters (threshold, gate, drop limit,
    /// cycle interval) live. Every change is applied immediately and persisted.
    /// </summary>
    private void HandleSubmenuAutoLeveler()
    {
        EnterSubmenu();
        try
        {
            AutoLeveler al = orchestrator.AutoLeveler;
            string status = al.IsEnabled ? "ON" : "OFF";
            PaintSubmenu(new string[]
            {
                "AutoLeveler",
                $"  Status: {status}   Threshold: {al.ThresholdDb:F1} dB   Gate: {al.GateThresholdDb:F1} dB",
                $"  Slew up: {al.SlewUpDbPerSec:F1} dB/s   down: {al.SlewDownDbPerSec:F1} dB/s   " +
                    $"Drop: {al.DropLimitDb:F1} dB   Cycle: {al.CycleIntervalMs} ms",
                "",
                "  [T] Toggle on/off   [S] Speed (slew)   [P] Parameters",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;
            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'T': orchestrator.ToggleAutoLeveler(); break;
                case 'S': AskAutoLevelerSpeed(); break;
                case 'P': AskAutoLevelerParameters(); break;
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Chooses the AutoLeveler's slew rate (how fast it corrects) from presets or a
    /// custom up/down pair. Applied live and persisted. Separating up and down lets
    /// the engineer, for example, pull a hot level down quickly but raise a quiet one
    /// gently.
    /// </summary>
    private void AskAutoLevelerSpeed()
    {
        AutoLeveler al = orchestrator.AutoLeveler;
        while (true)
        {
            PaintSubmenu(new string[]
            {
                "AutoLeveler speed (slew rate, dB per second)",
                $"  Current: up {al.SlewUpDbPerSec:F1}, down {al.SlewDownDbPerSec:F1}",
                "",
                "  [1] Smooth 0.5 / 0.5      [2] Normal 2 / 2      [3] Fast 6 / 6",
                "  [4] Protective: down 5, up 1      [5] Custom",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (key.KeyChar)
            {
                case '1': ApplySlew(0.5, 0.5); break;
                case '2': ApplySlew(2.0, 2.0); break;
                case '3': ApplySlew(6.0, 6.0); break;
                case '4': ApplySlew(1.0, 5.0); break;
                case '5':
                    double? up = AskDouble("Upward slew (0.1-20 dB/s), Esc to cancel:", 0.1, 20.0);
                    if (up.HasValue)
                    {
                        double? down = AskDouble("Downward slew (0.1-20 dB/s), Esc to cancel:", 0.1, 20.0);
                        if (down.HasValue)
                        {
                            ApplySlew(up.Value, down.Value);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Applies a slew pair to the live module and the persistent config.</summary>
    private void ApplySlew(double up, double down)
    {
        orchestrator.AutoLeveler.SlewUpDbPerSec = up;
        orchestrator.AutoLeveler.SlewDownDbPerSec = down;
        orchestrator.Config.AutoLevelerSlewUpDbPerSec = up;
        orchestrator.Config.AutoLevelerSlewDownDbPerSec = down;
    }

    /// <summary>Edits the AutoLeveler's numeric parameters live (and persists them).</summary>
    private void AskAutoLevelerParameters()
    {
        AutoLeveler al = orchestrator.AutoLeveler;
        while (true)
        {
            PaintSubmenu(new string[]
            {
                "AutoLeveler parameters",
                $"  [1] Action threshold: {al.ThresholdDb:F1} dB",
                $"  [2] Silence gate: {al.GateThresholdDb:F1} dB",
                $"  [3] Drop limit: {al.DropLimitDb:F1} dB",
                $"  [4] Cycle interval: {al.CycleIntervalMs} ms",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (key.KeyChar)
            {
                case '1':
                    double? thr = AskDouble("Action threshold (0.1-24 dB), Esc to cancel:", 0.1, 24.0);
                    if (thr.HasValue) { al.ThresholdDb = thr.Value; orchestrator.Config.AutoLevelerThresholdDb = thr.Value; }
                    break;
                case '2':
                    double? gate = AskDouble("Silence gate (-90 to 0 dB), Esc to cancel:", -90.0, 0.0);
                    if (gate.HasValue) { al.GateThresholdDb = gate.Value; orchestrator.Config.AutoLevelerGateThresholdDb = gate.Value; }
                    break;
                case '3':
                    double? drop = AskDouble("Drop limit (0.1-60 dB), Esc to cancel:", 0.1, 60.0);
                    if (drop.HasValue) { al.DropLimitDb = drop.Value; orchestrator.Config.AutoLevelerDropLimitDb = drop.Value; }
                    break;
                case '4':
                    int? cycle = AskInt("Cycle interval (10-5000 ms), Esc to cancel:", 10, 5000);
                    if (cycle.HasValue) { al.CycleIntervalMs = cycle.Value; orchestrator.Config.AutoLevelerCycleIntervalMs = cycle.Value; }
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // AntiClipper submenu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AntiClipper submenu: toggle on/off and edit its numeric parameters (clip
    /// threshold, reduction and aggressive steps, severe-overshoot threshold,
    /// confirmation samples, cooldown) live. Changes are applied immediately and
    /// persisted.
    /// </summary>
    private void HandleSubmenuAntiClipper()
    {
        EnterSubmenu();
        try
        {
            AntiClipper acm = orchestrator.AntiClipper;
            string status = acm.IsEnabled ? "ON" : "OFF";
            PaintSubmenu(new string[]
            {
                "Anti-Clip (clip protection)",
                $"  Status: {status}   Clip: {acm.ClipThresholdDb:F1} dBFS   " +
                    $"Step: {acm.ReductionStepDb:F1}/{acm.MaxReductionStepDb:F1} dB   Cooldown: {acm.CooldownMs} ms",
                "",
                "  [T] Toggle on/off   [P] Parameters",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;
            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'T': orchestrator.ToggleAntiClipper(); break;
                case 'P': AskAntiClipperParameters(); break;
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>Edits the AntiClipper's numeric parameters live (and persists them).</summary>
    private void AskAntiClipperParameters()
    {
        AntiClipper acm = orchestrator.AntiClipper;
        while (true)
        {
            PaintSubmenu(new string[]
            {
                "Anti-Clip parameters",
                $"  [1] Clip threshold: {acm.ClipThresholdDb:F1} dBFS   [2] Reduction step: {acm.ReductionStepDb:F1} dB",
                $"  [3] Aggressive step: {acm.MaxReductionStepDb:F1} dB   [4] Severe overshoot: {acm.SevereOvershootDb:F1} dB",
                $"  [5] Confirmation samples: {acm.ConfirmationSamples}   [6] Cooldown: {acm.CooldownMs} ms",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (key.KeyChar)
            {
                case '1':
                    double? ct = AskDouble("Clip threshold (-20 to 0 dBFS), Esc to cancel:", -20.0, 0.0);
                    if (ct.HasValue) { acm.ClipThresholdDb = ct.Value; orchestrator.Config.AntiClipperClipThresholdDb = ct.Value; }
                    break;
                case '2':
                    // Keep the reduction step at or below the aggressive step.
                    double? rs = AskDouble($"Reduction step (0.1-{acm.MaxReductionStepDb:F1} dB), Esc to cancel:", 0.1, acm.MaxReductionStepDb);
                    if (rs.HasValue) { acm.ReductionStepDb = rs.Value; orchestrator.Config.AntiClipperReductionStepDb = rs.Value; }
                    break;
                case '3':
                    // Keep the aggressive step at or above the reduction step.
                    double? ms = AskDouble($"Aggressive step ({acm.ReductionStepDb:F1}-12 dB), Esc to cancel:", acm.ReductionStepDb, 12.0);
                    if (ms.HasValue) { acm.MaxReductionStepDb = ms.Value; orchestrator.Config.AntiClipperMaxReductionStepDb = ms.Value; }
                    break;
                case '4':
                    double? so = AskDouble("Severe overshoot (1-18 dB), Esc to cancel:", 1.0, 18.0);
                    if (so.HasValue) { acm.SevereOvershootDb = so.Value; orchestrator.Config.AntiClipperSevereOvershootDb = so.Value; }
                    break;
                case '5':
                    int? cs = AskInt("Confirmation samples (1-20), Esc to cancel:", 1, 20);
                    if (cs.HasValue) { acm.ConfirmationSamples = cs.Value; orchestrator.Config.AntiClipperConfirmationSamples = cs.Value; }
                    break;
                case '6':
                    int? cd = AskInt("Cooldown (100-30000 ms), Esc to cancel:", 100, 30000);
                    if (cd.HasValue) { acm.CooldownMs = cd.Value; orchestrator.Config.AntiClipperCooldownMs = cd.Value; }
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // BpmDetector submenu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BpmDetector submenu. Allows toggling on/off, changing the detection mode and
    /// changing the target channel. These are the engineer's decisions based on the
    /// available source (click track, percussive track, manual tap) and the show's
    /// channel layout.
    /// </summary>
    private void HandleSubmenuBpmDetector()
    {
        EnterSubmenu();
        try
        {
            string status = orchestrator.BpmDetector.IsEnabled ? "ON" : "OFF";
            string bpmText = orchestrator.BpmDetector.HasEverBeenValid
                ? $"{orchestrator.BpmDetector.CurrentBpm:F1}"
                : "---";

            PaintSubmenu(new string[]
            {
                "BPM Detector",
                "",
                $"  Status: {status}   Mode: {orchestrator.BpmDetector.Mode}   Channel: {orchestrator.BpmDetector.TargetChannel.Name}",
                $"  Current BPM: {bpmText}",
                "",
                "  [T] Toggle   [M] Mode   [C] Target channel   [P] Parameters   [F] Figure times",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'T': orchestrator.ToggleBpmDetector(); break;
                case 'M': AskBpmMode(); break;
                case 'C': AskBpmTargetChannel(); break;
                case 'P': AskBpmParameters(); break;
                case 'F': ShowFigureTimes(); break;
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Shows the delay time of every rhythmic figure at the established tempo —
    /// the table an engineer mentally computes before picking a figure for a slot.
    /// Times beyond the console's 3000 ms cap are marked, since the M32 saturates
    /// them silently.
    /// </summary>
    private void ShowFigureTimes()
    {
        Dictionary<BeatType, double> times = orchestrator.BpmDetector.GetDelayTimes();
        if (times.Count == 0)
        {
            ShowNotice("No tempo detected yet. Tap [T] in the main menu or enable the detector.");
            return;
        }

        string Cell(BeatType beat)
        {
            double raw = times[beat];
            double clamped = DelayEffects.ClampTimeMs(raw);
            return raw > DelayEffects.TIME_MAX_MS ? $"{clamped,4:F0}ms*" : $"{clamped,4:F0}ms ";
        }

        ShowNotice(
            $"Delay times at {orchestrator.BpmDetector.CurrentBpm:F1} BPM (* = capped at the console's 3000 ms):",
            $"  Whole    {Cell(BeatType.Whole)}  dotted {Cell(BeatType.DottedWhole)}  triplet {Cell(BeatType.WholeTriplet)}",
            $"  Half     {Cell(BeatType.Half)}  dotted {Cell(BeatType.DottedHalf)}  triplet {Cell(BeatType.HalfTriplet)}",
            $"  Quarter  {Cell(BeatType.Quarter)}  dotted {Cell(BeatType.DottedQuarter)}  triplet {Cell(BeatType.QuarterTriplet)}",
            $"  Eighth   {Cell(BeatType.Eighth)}  dotted {Cell(BeatType.DottedEighth)}  triplet {Cell(BeatType.EighthTriplet)}",
            $"  Slapback {Cell(BeatType.Slapback)}  dotted {Cell(BeatType.DottedSlapback)}  triplet {Cell(BeatType.SlapbackTriplet)}");
    }

    /// <summary>
    /// Asks for the BPM detection mode. Three options: Click, BeatTracking and
    /// ManualTap. Any other key cancels.
    /// </summary>
    private void AskBpmMode()
    {
        PaintSubmenu(new string[]
        {
            "Change BPM detection mode",
            "",
            "  [1] Click (each transient = 1 exact quarter note)",
            "  [2] BeatTracking (infer tempo from a rhythmic pattern)",
            "  [3] ManualTap (manual tempo with the T key)",
            "  [Esc] Cancel"
        });

        ConsoleKeyInfo key = input.ReadKey();
        if (key.Key == ConsoleKey.Escape) return;

        BpmDetectionMode? newMode = key.KeyChar switch
        {
            '1' => BpmDetectionMode.Click,
            '2' => BpmDetectionMode.BeatTracking,
            '3' => BpmDetectionMode.ManualTap,
            _ => (BpmDetectionMode?)null
        };

        if (newMode.HasValue)
        {
            orchestrator.SetBpmMode(newMode.Value);
        }
    }

    /// <summary>Edits the BpmDetector's numeric parameters live (and persists them).</summary>
    private void AskBpmParameters()
    {
        BpmDetector bd = orchestrator.BpmDetector;
        while (true)
        {
            PaintSubmenu(new string[]
            {
                "BPM Detector parameters",
                $"  [1] Transient threshold: {bd.TransientThresholdDb:F1} dBFS",
                $"  [2] Change hysteresis: {bd.BpmChangeHysteresis:F1} BPM",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (key.KeyChar)
            {
                case '1':
                    double? tt = AskDouble("Transient threshold (-60 to 0 dBFS), Esc to cancel:", -60.0, 0.0);
                    if (tt.HasValue) { bd.TransientThresholdDb = tt.Value; orchestrator.Config.BpmTransientThresholdDb = tt.Value; }
                    break;
                case '2':
                    double? hy = AskDouble("Change hysteresis (0-20 BPM), Esc to cancel:", 0.0, 20.0);
                    if (hy.HasValue) { bd.BpmChangeHysteresis = hy.Value; orchestrator.Config.BpmChangeHysteresis = hy.Value; }
                    break;
            }
        }
    }

    /// <summary>
    /// Asks for the OSC number of the rhythmic reference channel. If the number is
    /// not a watched channel, it offers to add it, asking for a name.
    /// </summary>
    private void AskBpmTargetChannel()
    {
        int? oscNumber = AskInt("OSC number of the target channel (1-32), Esc to cancel:", 1, 32);
        if (!oscNumber.HasValue) return;

        MixerChannel? channel = null;
        foreach (AudioDevice d in orchestrator.MeterReader.Devices)
        {
            if (d is MixerChannel c && c.OscNumber == oscNumber.Value)
            {
                channel = c;
                break;
            }
        }

        if (channel == null)
        {
            string? name = AskString($"Name for channel {oscNumber.Value} [Channel {oscNumber.Value}]:");
            if (name == null) return;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Channel {oscNumber.Value}";
            }
            channel = new MixerChannel(name, oscNumber.Value, -18.0, 0.0);
            orchestrator.AddChannel(channel);
        }

        orchestrator.SetBpmTargetChannel(channel);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Preferences submenu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Network and file-location preferences submenu. Shows IP, ports and data
    /// folder. Allows changing the mixer IP on the fly, opening the data folder in
    /// the system explorer, and starting a fresh session while keeping the
    /// connection. Ports cannot be changed on the fly because the UDP sockets are
    /// bound to them; changing them requires a restart.
    /// </summary>
    private void HandleSubmenuPreferences()
    {
        EnterSubmenu();
        try
        {
            string ipLine = $"  Mixer IP: {orchestrator.Config.MixerIp}"
                + (orchestrator.SimulationMode ? "   (not used in simulation)" : "");

            PaintSubmenu(new string[]
            {
                "Preferences",
                "",
                ipLine,
                $"  Send port: {orchestrator.Config.MixerSendPort}   Receive port: {orchestrator.Config.MixerReceivePort}",
                $"  Data folder: {AppPaths.AppDirectory}",
                "",
                "  [I] Change IP   [O] Open folder   [N] New session   [V] Diagnostics   [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'I':
                    string? newIp = AskString("New mixer IP (Esc to cancel):");
                    if (newIp != null)
                    {
                        if (SessionConfig.IsValidMixerIp(newIp))
                        {
                            orchestrator.SetMixerIp(newIp.Trim());
                        }
                        else
                        {
                            ShowNotice("Invalid IP address. The mixer IP was not changed.");
                        }
                    }
                    break;
                case 'O':
                    bool opened = AppPaths.OpenAppDirectoryInExplorer();
                    ShowNotice(opened
                        ? "Folder opened in the file explorer."
                        : "Could not open the folder on this system.");
                    break;
                case 'V':
                    ShowNotice(
                        $"Link: {orchestrator.OscClient.ReceivedCount} received, {orchestrator.OscClient.SentCount} sent",
                        $"Rejected packets: {orchestrator.OscClient.RejectedCount}; send failures: {orchestrator.OscClient.SendFailureCount}",
                        $"Control writes: {orchestrator.OscClient.ControlSentCount}; released: {orchestrator.OscClient.WritesInhibited}",
                        $"Log drops: {orchestrator.Logger.DroppedEvents}; disk: {orchestrator.Logger.DiskError ?? "OK"}");
                    break;
                case 'N':
                    AskResetSession();
                    break;
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Confirms with the engineer that they want to start a fresh session and, if
    /// confirmed, invokes <see cref="AppOrchestrator.ResetSession"/>. The
    /// confirmation is needed because the operation removes every configured
    /// channel, bus and delay slot (though the IP, ports and files on disk are kept).
    /// </summary>
    private void AskResetSession()
    {
        PaintSubmenu(new string[]
        {
            "Start a new session?",
            "",
            "  All watched channels, buses and configured slots will be removed.",
            "  The IP, ports and files on disk are kept.",
            "",
            "  [Y] Yes, start fresh   [N] No, cancel"
        });

        ConsoleKeyInfo key = input.ReadKey();
        if (key.Key == ConsoleKey.Escape) return;
        if (char.ToUpperInvariant(key.KeyChar) != 'Y') return;

        orchestrator.ResetSession();

        ShowNotice(
            "Session reset.",
            "",
            "  You can add channels and buses from [N] in the main menu.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Channels and buses submenu
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Submenu for managing watched channels and buses. Allows adding, renaming and
    /// removing devices during the show without restarting.
    /// </summary>
    private void HandleSubmenuChannels()
    {
        EnterSubmenu();
        try
        {
            PaintSubmenu(new string[]
            {
                "Watched channels and buses",
                "",
                "  [A] Add channel     [B] Add bus",
                "  [R] Rename          [X] Remove     [E] Control and limits",
                "  [Esc] Back"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;

            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'A': AddChannelFlow(); break;
                case 'B': AddBusFlow(); break;
                case 'R': RenameDeviceFlow(); break;
                case 'X': RemoveDeviceFlow(); break;
                case 'E': EditDeviceFlow(); break;
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Add-channel flow: asks for a number (1-32) and a name. If the name is left
    /// empty, "Channel N" is used by default.
    /// </summary>
    private void EditDeviceFlow()
    {
        PaintSubmenu(new[] { "Control and limits", "[C] Channel  [B] Bus  [Esc] Cancel" });
        var key = input.ReadKey();
        bool bus = char.ToUpperInvariant(key.KeyChar) == 'B';
        if (!bus && char.ToUpperInvariant(key.KeyChar) != 'C') return;
        int? number = AskInt("Device number:", 1, bus ? 16 : 32);
        if (number == null) return;
        var device = orchestrator.MeterReader.Devices.FirstOrDefault(d => d.OscNumber == number && (d is Bus) == bus);
        if (device == null) { ShowNotice("Device is not watched."); return; }
        while (true)
        {
            PaintSubmenu(new[] {
                $"{device.Name}: control and limits",
                $"[A] Level: {device.AutoLevelEnabled}   [C] Protection: {device.ProtectionEnabled}",
                $"[T] Target: {device.TargetLevelDb:F1} dBFS   [F] Max fader: {device.MaxFaderDb:F1} dB",
                $"[G] Maximum accumulated gain reduction: {device.MaxGainReductionDb:F1} dB",
                device is MixerChannel channel && channel.HeadAmpResolved
                    ? "Gain shared with: " + string.Join(", ", orchestrator.MeterReader.Devices.OfType<MixerChannel>().Where(c => ReferenceEquals(c.Gain, channel.Gain)).Select(c => $"#{c.OscNumber} {c.Name}"))
                    : "",
                "[Esc] Back   [F12] PANIC"
            });
            key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;
            double? value;
            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'A': lock (orchestrator.OscClient.ControlLock) device.AutoLevelEnabled = !device.AutoLevelEnabled; break;
                case 'C': lock (orchestrator.OscClient.ControlLock) device.ProtectionEnabled = !device.ProtectionEnabled; break;
                case 'T':
                    value = AskDouble("Target (-90 to 10 dBFS):", -90, 10);
                    if (value.HasValue) device.TargetLevelDb = value.Value;
                    break;
                case 'F':
                    value = AskDouble("Maximum fader (-90 to 10 dB):", -90, 10);
                    if (value.HasValue) device.MaxFaderDb = value.Value;
                    break;
                case 'G':
                    value = AskDouble("Maximum total gain reduction (0 to 72 dB):", 0, 72);
                    if (value.HasValue) device.MaxGainReductionDb = value.Value;
                    break;
            }
        }
    }

    private void AddChannelFlow()
    {
        int? num = AskInt("Channel number (1-32), Esc to cancel:", 1, 32);
        if (!num.HasValue) return;

        string? name = AskString($"Name for channel {num.Value} [Channel {num.Value}]:");
        if (name == null) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Channel {num.Value}";
        }

        MixerChannel channel = new MixerChannel(name, num.Value, -18.0, 0.0);
        if (!orchestrator.AddChannel(channel))
        {
            ShowNotice($"Channel {num.Value} is already being watched.");
        }
    }

    /// <summary>
    /// Add-bus flow: asks for a number (1-16) and a name. If the name is left empty,
    /// "Bus N" is used by default.
    /// </summary>
    private void AddBusFlow()
    {
        int? num = AskInt("Bus number (1-16), Esc to cancel:", 1, 16);
        if (!num.HasValue) return;

        string? name = AskString($"Name for bus {num.Value} [Bus {num.Value}]:");
        if (name == null) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Bus {num.Value}";
        }

        Bus bus = new Bus(name, num.Value, -18.0);
        if (!orchestrator.AddBus(bus))
        {
            ShowNotice($"Bus {num.Value} is already being watched.");
        }
    }

    /// <summary>
    /// Rename flow: asks for the type (channel/bus), number and new name. If the
    /// device does not exist, shows a brief notice.
    /// </summary>
    private void RenameDeviceFlow()
    {
        PaintSubmenu(new string[]
        {
            "Rename device",
            "",
            "  [C] Channel   [B] Bus   [Esc] Cancel"
        });

        ConsoleKeyInfo typeKey = input.ReadKey();
        if (typeKey.Key == ConsoleKey.Escape) return;

        char typeChar = char.ToUpperInvariant(typeKey.KeyChar);
        string typeKeyword;
        int min, max;

        if (typeChar == 'C') { typeKeyword = "channel"; min = 1; max = 32; }
        else if (typeChar == 'B') { typeKeyword = "bus"; min = 1; max = 16; }
        else return;

        int? num = AskInt($"Number ({min}-{max}), Esc to cancel:", min, max);
        if (!num.HasValue) return;

        string? newName = AskString("New name, Esc to cancel:");
        if (string.IsNullOrWhiteSpace(newName)) return;

        bool renamed = orchestrator.RenameDevice(typeKeyword, num.Value, newName.Trim());

        ShowNotice(renamed
            ? "Renamed successfully."
            : $"No {(typeKeyword == "channel" ? "channel" : "bus")} with that number.");
    }

    /// <summary>
    /// Remove flow: asks for the type (channel/bus) and number. Finds the device in
    /// the watched list and removes it from the modules.
    /// </summary>
    private void RemoveDeviceFlow()
    {
        PaintSubmenu(new string[]
        {
            "Remove device",
            "",
            "  [C] Channel   [B] Bus   [Esc] Cancel"
        });

        ConsoleKeyInfo typeKey = input.ReadKey();
        if (typeKey.Key == ConsoleKey.Escape) return;

        char typeChar = char.ToUpperInvariant(typeKey.KeyChar);

        if (typeChar == 'C')
        {
            int? num = AskInt("Channel number to remove (1-32), Esc to cancel:", 1, 32);
            if (!num.HasValue) return;

            foreach (AudioDevice d in orchestrator.MeterReader.Devices)
            {
                if (d is MixerChannel c && c.OscNumber == num.Value)
                {
                    orchestrator.RemoveChannel(c);
                    return;
                }
            }

            ShowNotice($"No channel {num.Value} is being watched.");
        }
        else if (typeChar == 'B')
        {
            int? num = AskInt("Bus number to remove (1-16), Esc to cancel:", 1, 16);
            if (!num.HasValue) return;

            foreach (AudioDevice d in orchestrator.MeterReader.Devices)
            {
                if (d is Bus b && b.OscNumber == num.Value)
                {
                    orchestrator.RemoveBus(b);
                    return;
                }
            }

            ShowNotice($"No bus {num.Value} is being watched.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Delay slots
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures a delay slot. Asks for the slot number (1-4: slots 5-8 are
    /// insert-only and can never hold a delay, so offering them would only
    /// configure a slot that no delay time can ever reach), the delay effect
    /// loaded in that slot on the console (so the time hits the right parameter),
    /// the mode (automatic or manual) and the mode's parameters (rhythmic figure
    /// if automatic, milliseconds if manual).
    /// </summary>
    private void HandleConfigureDelaySlot()
    {
        EnterSubmenu();
        try
        {
            int? slot = AskInt(
                "Slot number (1-4; slots 5-8 cannot hold delays), Esc to cancel:",
                1, DelayController.MAX_DELAY_SLOT_NUMBER);
            if (!slot.HasValue) return;

            DelayEffectType? effectType = ResolveSlotEffectType(slot.Value);
            if (!effectType.HasValue) return;

            PaintSubmenu(new string[]
            {
                $"Slot {slot.Value}: {effectType.Value}",
                "",
                "  [A] Automatic (synced to BPM)",
                "  [M] Manual (fixed ms)",
                "  [Esc] Cancel"
            });

            ConsoleKeyInfo modeKey = input.ReadKey();
            if (modeKey.Key == ConsoleKey.Escape) return;

            char modeChar = char.ToUpperInvariant(modeKey.KeyChar);

            if (modeChar == 'A')
            {
                BeatType? beat = AskBeatType();
                if (!beat.HasValue) return;
                orchestrator.ConfigureDelaySlot(slot.Value, DelaySlotConfig.Automatic(effectType.Value, beat.Value));
            }
            else if (modeChar == 'M')
            {
                double? ms = AskDouble("Manual time (ms, between 1 and 3000), Esc to cancel:", 1, 3000);
                if (!ms.HasValue) return;
                orchestrator.ConfigureDelaySlot(slot.Value, DelaySlotConfig.Manual(effectType.Value, ms.Value));
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Determines the delay effect type for a slot: uses the effect auto-detected
    /// from the console when it is a delay, otherwise asks the engineer (fallback).
    /// </summary>
    private DelayEffectType? ResolveSlotEffectType(int slot)
    {
        IReadOnlyDictionary<int, DetectedEffect> detected = orchestrator.DelayController.DetectedEffects;
        bool haveDetection = detected.TryGetValue(slot, out DetectedEffect? fx);

        if (haveDetection && fx!.DelayType.HasValue)
        {
            return fx.DelayType.Value; // auto-detected delay; no need to ask
        }

        string note = haveDetection
            ? $"console reports '{fx!.Name}', not a delay"
            : "not detected from the console yet";
        return AskDelayEffectType(slot, note);
    }

    /// <summary>
    /// Asks which delay effect is loaded in the slot. Used as a fallback when the
    /// console has not reported a delay for it. Must match the M32 FX rack, because
    /// each delay keeps its time on a different parameter. Any other key cancels.
    /// </summary>
    private DelayEffectType? AskDelayEffectType(int slot, string note)
    {
        PaintSubmenu(new string[]
        {
            $"Slot {slot}: pick the delay effect ({note})",
            "  (must match the effect in the M32 FX rack)",
            "",
            "  [1] Stereo Delay     [2] 3-Tap Delay      [3] 4-Tap Delay",
            "  [4] Delay+Chamber    [5] Delay+Chorus     [6] Delay+Flanger",
            "  [7] Modulation Delay     [Esc] Cancel"
        });

        ConsoleKeyInfo key = input.ReadKey();
        if (key.Key == ConsoleKey.Escape) return null;

        return key.KeyChar switch
        {
            '1' => DelayEffectType.StereoDelay,
            '2' => DelayEffectType.ThreeTapDelay,
            '3' => DelayEffectType.FourTapDelay,
            '4' => DelayEffectType.DelayChamber,
            '5' => DelayEffectType.DelayChorus,
            '6' => DelayEffectType.DelayFlanger,
            '7' => DelayEffectType.ModulationDelay,
            _ => (DelayEffectType?)null
        };
    }

    /// <summary>
    /// Asks for the rhythmic figure in two steps: first the family (Whole/Half/
    /// Quarter/Eighth/Slapback) and then the variant (Straight/Dotted/Triplet).
    /// Fifteen figures total with two key presses.
    /// </summary>
    private BeatType? AskBeatType()
    {
        PaintSubmenu(new string[]
        {
            "Rhythmic figure",
            "",
            "  [1] Whole   [2] Half   [3] Quarter   [4] Eighth   [5] Slapback",
            "  [Esc] Cancel"
        });

        ConsoleKeyInfo familyKey = input.ReadKey();
        if (familyKey.Key == ConsoleKey.Escape) return null;

        switch (familyKey.KeyChar)
        {
            case '1': return AskBeatVariant(BeatType.Whole, BeatType.DottedWhole, BeatType.WholeTriplet);
            case '2': return AskBeatVariant(BeatType.Half, BeatType.DottedHalf, BeatType.HalfTriplet);
            case '3': return AskBeatVariant(BeatType.Quarter, BeatType.DottedQuarter, BeatType.QuarterTriplet);
            case '4': return AskBeatVariant(BeatType.Eighth, BeatType.DottedEighth, BeatType.EighthTriplet);
            case '5': return AskBeatVariant(BeatType.Slapback, BeatType.DottedSlapback, BeatType.SlapbackTriplet);
            default: return null;
        }
    }

    /// <summary>
    /// Second step of figure selection: given the family (its three variants), asks
    /// which one to apply.
    /// </summary>
    private BeatType? AskBeatVariant(BeatType straight, BeatType dotted, BeatType triplet)
    {
        PaintSubmenu(new string[]
        {
            "Variant",
            "",
            "  [N] Straight   [P] Dotted   [T] Triplet",
            "  [Esc] Cancel"
        });

        ConsoleKeyInfo varKey = input.ReadKey();
        if (varKey.Key == ConsoleKey.Escape) return null;

        switch (char.ToUpperInvariant(varKey.KeyChar))
        {
            case 'N': return straight;
            case 'P': return dotted;
            case 'T': return triplet;
            default: return null;
        }
    }

    /// <summary>
    /// Frees a previously configured delay slot, leaving it without automatic
    /// management.
    /// </summary>
    private void HandleClearDelaySlot()
    {
        EnterSubmenu();
        try
        {
            int? slot = AskInt("Slot number to clear (1-8), Esc to cancel:", 1, 8);
            if (slot.HasValue)
            {
                orchestrator.ClearDelaySlot(slot.Value);
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Re-queries the console for the effect loaded in each FX slot and shows the
    /// result. Use after changing effects on the console during the show.
    /// </summary>
    private void HandleRefreshEffects()
    {
        EnterSubmenu();
        try
        {
            orchestrator.RefreshEffects();
            // Give the console a moment to answer the /fx/N/type queries.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 500)
            {
                if (Console.KeyAvailable && input.ReadKey().Key == ConsoleKey.Escape) return;
                Thread.Sleep(10);
            }
            ShowDetectedEffects();
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Paints the effects detected in FX slots 1-4 (the only ones that can hold
    /// delays), flagging which are delays. Slots 5-8 are insert-only.
    /// </summary>
    private void ShowDetectedEffects()
    {
        IReadOnlyDictionary<int, DetectedEffect> detected = orchestrator.DelayController.DetectedEffects;

        List<string> lines = new List<string>
        {
            "Detected FX effects (slots 1-4 can hold delays):"
        };

        for (int slot = 1; slot <= DelayController.MAX_DELAY_SLOT_NUMBER; slot++)
        {
            if (detected.TryGetValue(slot, out DetectedEffect? fx))
            {
                lines.Add($"  Slot {slot}: {fx.Name}  [{(fx.IsDelay ? "delay" : "no delay")}]");
            }
            else
            {
                lines.Add($"  Slot {slot}: (not reported by console)");
            }
        }

        lines.Add("  Slots 5-8 are insert effects (no delays).");

        ShowNotice(lines.ToArray());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Direct actions (no submenu)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Manual tap tempo: records a press on the BPM detector. Does not open a
    /// submenu, to allow fast, repeated tapping without interrupting interaction.
    /// </summary>
    private void HandleTapTempo()
    {
        orchestrator.TapTempo();
    }

    /// <summary>
    /// Panic / release-all: instantly disables every automatic module so the engineer
    /// regains full manual control. No confirmation — that is the point of a panic
    /// key. Shows a brief notice; faders and preamps keep their current positions.
    /// </summary>
    private void HandlePanic()
    {
        orchestrator.ReleaseAll();

        EnterSubmenu();
        try
        {
            ShowNotice(
                "ALL AUTOMATIC CONTROL RELEASED",
                "",
                "  AutoLeveler, Anti-Clip and BPM Detector are now OFF.",
                "  Faders and preamps are left exactly where they are.");
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Recalibrates the AutoLeveler targets by capturing the current levels as new
    /// targets. Blocks for 2 seconds during capture, showing it on screen.
    /// </summary>
    private void HandleRecalibrate()
    {
        EnterSubmenu();
        try
        {
            PaintSubmenu(new string[]
            {
                "Recalibrating AutoLeveler targets...",
                "  Wait 2 seconds without touching anything to capture levels."
            });
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < AutoLeveler.DEFAULT_TARGET_CAPTURE_MS)
            {
                if (Console.KeyAvailable && input.ReadKey().Key == ConsoleKey.Escape) return;
                Thread.Sleep(10);
            }
            if (orchestrator.OscClient.WritesInhibited) return;
            orchestrator.AutoLeveler.CaptureTargets(0);
            ShowNotice("Recalibration complete.");
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Saves the current configuration as a preset with the name given by the user.
    /// The file is created in the app's presets folder.
    /// </summary>
    private void HandleSaveConfig()
    {
        EnterSubmenu();
        try
        {
            string? name = AskString("Save preset. Name (no extension), Esc to cancel:");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                orchestrator.SavePresetAs(name.Trim());
                ShowNotice($"Saved as preset '{name.Trim()}'.");
            }
            catch (Exception ex)
            {
                ShowNotice($"Error: {ex.Message}");
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Loads an existing preset and applies its full configuration, replacing the
    /// device list, the modules' parameters and the delay slots.
    /// </summary>
    private void HandleLoadConfig()
    {
        EnterSubmenu();
        try
        {
            string available = Directory.Exists(AppPaths.PresetsDirectory)
                ? string.Join(", ", Directory.EnumerateFiles(AppPaths.PresetsDirectory, "*.json").Select(Path.GetFileNameWithoutExtension))
                : "(none)";
            PaintSubmenu(new[] { "Available presets", available });
            string? name = AskString("Load preset. Name (no extension), Esc to cancel:");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                var preview = AppOrchestrator.ReadSession(AppPaths.GetPresetPath(name.Trim()));
                PaintSubmenu(new[] {
                    $"Apply preset '{name.Trim()}'?",
                    $"Devices: {orchestrator.MeterReader.Devices.Count} -> {preview.WatchedDevices.Count}; window: {orchestrator.MeterReader.WindowMs} -> {preview.MeterWindowMs} ms",
                    $"Level: {orchestrator.AutoLeveler.IsEnabled} -> {preview.AutoLevelerEnabled}; clip: {orchestrator.AntiClipper.IsEnabled} -> {preview.AntiClipperEnabled}; BPM: {orchestrator.BpmDetector.IsEnabled} -> {preview.BpmDetectorEnabled}",
                    $"Delay slots: {orchestrator.DelayController.DelaySlots.Count} -> {preview.DelaySlots.Count}. Network settings are kept.",
                    "[Y] Apply   [Esc] Cancel   [F12] PANIC"
                });
                if (char.ToUpperInvariant(input.ReadKey().KeyChar) != 'Y') return;
                orchestrator.ApplyLoadedConfig(preview);
                ShowNotice($"Loaded preset '{name.Trim()}'.");
            }
            catch (Exception ex)
            {
                ShowNotice($"Error: {ex.Message}");
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    /// <summary>
    /// Confirms exiting the program. If the user accepts, sets the
    /// <see cref="shouldExit"/> flag and the main loop ends, returning control to
    /// <see cref="Program"/> so it runs the clean shutdown.
    /// </summary>
    private void HandleQuit()
    {
        EnterSubmenu();
        try
        {
            PaintSubmenu(new string[]
            {
                "Close the show and save the session?",
                "",
                "  [Y] Yes   [N] No"
            });

            ConsoleKeyInfo key = input.ReadKey();
            if (key.Key == ConsoleKey.Escape) return;
            if (char.ToUpperInvariant(key.KeyChar) == 'Y')
            {
                shouldExit = true;
            }
        }
        finally
        {
            ExitSubmenu();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Submenu painting infrastructure
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enters submenu mode: pauses the dashboard and clears the reserved area.
    /// Should be called at the start of each submenu (ideally inside a try/finally
    /// alongside <see cref="ExitSubmenu"/>).
    /// </summary>
    private void EnterSubmenu()
    {
        dashboard.Pause();
        ClearSubmenuArea();
        dashboard.ClearRow(dashboard.MenuPromptRow);
    }

    /// <summary>
    /// Leaves submenu mode: clears the area, clears the main prompt (so the
    /// dashboard can repaint clean) and resumes the dashboard refresh.
    /// </summary>
    private void ExitSubmenu()
    {
        ClearSubmenuArea();
        dashboard.ClearRow(dashboard.MenuPromptRow);
        dashboard.Resume();
    }

    /// <summary>
    /// Clears every row of the area reserved for submenus.
    /// </summary>
    private void ClearSubmenuArea()
    {
        for (int i = 0; i < dashboard.SubmenuAreaLines; i++)
        {
            dashboard.ClearRow(dashboard.SubmenuFirstRow + i);
        }
    }

    /// <summary>
    /// Paints a full submenu: clears the area and writes each line on its row. If
    /// <paramref name="lines"/> has more elements than the available area, the extra
    /// ones are truncated.
    /// </summary>
    /// <param name="lines">Lines to paint, top to bottom.</param>
    private void PaintSubmenu(string[] lines)
    {
        ClearSubmenuArea();
        int count = Math.Min(lines.Length, dashboard.SubmenuAreaLines);
        for (int i = 0; i < count; i++)
        {
            dashboard.WriteToRow(dashboard.SubmenuFirstRow + i, lines[i]);
        }
    }

    /// <summary>
    /// Paints a notice and waits for a key press to dismiss it. Used for every
    /// informational message so the operator reads it at their own pace and moves
    /// on with a single key, instead of waiting out a fixed pause. The notice can
    /// hold at most <see cref="ConsoleDashboard.SubmenuAreaLines"/> - 2 lines (the
    /// last two rows hold the dismiss prompt).
    /// </summary>
    private void ShowNotice(params string[] lines)
    {
        string[] withPrompt = new string[lines.Length + 2];
        Array.Copy(lines, withPrompt, lines.Length);
        withPrompt[^2] = "";
        withPrompt[^1] = "  Press any key to continue...";
        PaintSubmenu(withPrompt);
        input.ReadKey();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Input helpers with cancel (Esc)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a line of text from the user, painting a prompt on the last row of the
    /// submenu area. Supports Backspace to correct, Enter to confirm and Esc to
    /// cancel (returns <c>null</c>).
    /// </summary>
    /// <remarks>
    /// Implements the read loop manually with <see cref="Console.ReadKey"/> instead
    /// of <see cref="Console.ReadLine"/> because ReadLine cannot detect Esc: if the
    /// user presses Esc, ReadLine does not return until Enter. Reading key by key
    /// allows cancelling immediately on Esc.
    /// </remarks>
    /// <returns>The entered text, or <c>null</c> if the user cancelled with Esc.</returns>
    private string? AskString(string prompt)
    {
        int row = dashboard.SubmenuFirstRow + dashboard.SubmenuAreaLines - 1;
        dashboard.WriteToRow(row, prompt + " ");

        int promptLen = prompt.Length + 1;
        dashboard.MoveCursorTo(row, promptLen + 1);
        dashboard.ShowCursor();

        try
        {
            StringBuilder buffer = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = input.ReadKey();

                if (key.Key == ConsoleKey.Escape)
                {
                    return null;
                }
                if (key.Key == ConsoleKey.Enter)
                {
                    return buffer.ToString();
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length = buffer.Length - 1;
                        int col = promptLen + buffer.Length + 1;
                        dashboard.MoveCursorTo(row, col);
                        Console.Write(' ');
                        dashboard.MoveCursorTo(row, col);
                    }
                    continue;
                }
                if (key.KeyChar >= ' ' && key.KeyChar != (char)127)
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
        finally
        {
            dashboard.HideCursor();
        }
    }

    /// <summary>
    /// Asks the user for an integer and validates it is within the given range. If
    /// the user cancels with Esc, returns <c>null</c>. If the value is invalid or
    /// out of range, shows a brief notice and also returns <c>null</c>.
    /// </summary>
    private int? AskInt(string prompt, int min, int max)
    {
        string? line = AskString(prompt);
        if (line == null) return null;

        if (int.TryParse(line.Trim(), out int value) && value >= min && value <= max)
        {
            return value;
        }

        ShowNotice($"Invalid value. Must be between {min} and {max}.");
        return null;
    }

    /// <summary>
    /// Asks the user for a decimal and validates it is within the given range.
    /// Accepts both the comma and the dot as the decimal separator (the comma is
    /// normalized to a dot before parsing), so it behaves the same regardless of the
    /// operator's locale.
    /// </summary>
    private double? AskDouble(string prompt, double min, double max)
    {
        string? line = AskString(prompt);
        if (line == null) return null;

        // Accept both decimal separators by normalizing the comma to a dot, then
        // parse with the invariant culture. NumberStyles.Float deliberately omits
        // AllowThousands so a comma is never silently read as a group separator
        // (e.g. "1,5" must be 1.5, not 15).
        string normalized = line.Trim().Replace(',', '.');

        if (double.TryParse(normalized, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value >= min && value <= max)
        {
            return value;
        }

        ShowNotice($"Invalid value. Must be between {min} and {max}.");
        return null;
    }
}
