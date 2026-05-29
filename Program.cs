using M32LiveConsoleTool.Infrastructure;
using M32LiveConsoleTool.Ui;

namespace M32LiveConsoleTool;

/// <summary>
/// Application entry point. Orchestrates an ordered startup of the components:
/// asks the user whether to run in simulation mode, runs the first-run wizard if
/// there is no previous configuration, initializes the
/// <see cref="AppOrchestrator"/> and starts the dashboard and interactive menu.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Startup order</strong>:
/// </para>
/// <list type="number">
///   <item>Create the app data folders if they do not exist.</item>
///   <item>Ask the user whether to run in simulation mode (Y/N).</item>
///   <item>If there is no <c>last_session.json</c>, run the
///   <see cref="FirstRunWizard"/> and save its result.</item>
///   <item>Create the <see cref="AppOrchestrator"/> and initialize every module.</item>
///   <item>Register shutdown handlers for Ctrl+C and ProcessExit, so the session
///   is saved even if the user closes the app abruptly.</item>
///   <item>Start the orchestrator (which starts all modules in order).</item>
///   <item>Start the dashboard (enters the terminal's alternate buffer).</item>
///   <item>Enter the menu loop, which blocks the main thread until the user exits.</item>
///   <item>Shut down cleanly when the menu returns.</item>
/// </list>
/// <para>
/// <strong>Shutdown handlers</strong>: two are registered.
/// <see cref="OnCancelKeyPress"/> captures Ctrl+C, neutralizes it with
/// <c>e.Cancel = true</c> and triggers the shutdown manually, preventing the
/// terminal from killing the process halfway and leaving files unclosed.
/// <see cref="OnProcessExit"/> captures process exit by any other route (closing
/// the window, kill, etc.) and also triggers the shutdown. Both call the same
/// <see cref="AppOrchestrator.Shutdown"/>, which is idempotent.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Static reference to the orchestrator so the shutdown handlers (Ctrl+C and
    /// ProcessExit) can invoke its shutdown without receiving the object as a
    /// parameter (OS event handlers take no extra parameters).
    /// </summary>
    private static AppOrchestrator? orchestrator;

    /// <summary>
    /// Static reference to the dashboard so the shutdown handlers (Ctrl+C and
    /// ProcessExit) can leave the alternate screen and restore the cursor. Without
    /// this, an abrupt exit would leave the terminal stuck in the dashboard's
    /// full-screen buffer. <see cref="ConsoleDashboard.Stop"/> is idempotent, so it
    /// is safe to call from both a handler and the main <c>finally</c>.
    /// </summary>
    private static ConsoleDashboard? dashboard;

    /// <summary>
    /// Entry method. Coordinates the whole startup and shutdown of the program.
    /// </summary>
    public static void Main(string[] args)
    {
        Console.Title = "M32 Live Console Tool";

        AppPaths.EnsureDirectoriesExist();

        bool useSimulator = AskSimulationMode();

        // The first-run wizard only makes sense for a real console (mixer IP, ports,
        // channel layout). In simulation the orchestrator loads a demo session
        // directly, so neither the wizard nor a saved file is needed.
        if (!useSimulator && !AppOrchestrator.HasExistingConfig())
        {
            SessionConfig initial = FirstRunWizard.Run();
            AppOrchestrator.WriteSession(initial, AppPaths.LastSessionFile);
        }

        orchestrator = new AppOrchestrator();
        orchestrator.Initialize(useSimulator);

        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Exception? fatalError = null;

        try
        {
            orchestrator.Start();

            try
            {
                Console.Clear();
            }
            catch
            {
            }

            ConsoleDashboard dash = new ConsoleDashboard(orchestrator);
            dashboard = dash; // publish for the Ctrl+C and ProcessExit handlers
            dash.Start();

            ConsoleMenu menu = new ConsoleMenu(orchestrator, dash);
            menu.Run();
        }
        catch (Exception ex)
        {
            // Last line of defense. An unhandled error must never leave the
            // terminal stuck in the alternate screen with the cursor hidden, nor
            // the modules running against a dead socket. We capture it here, log
            // it while the logger is still open, and report it to the user only
            // after the finally block has restored the terminal.
            fatalError = ex;
            try
            {
                orchestrator.Logger?.LogEvent($"Program|fatal|{ex}", Severity.Error);
            }
            catch
            {
            }
        }
        finally
        {
            // Always restore the terminal and release resources, even on a crash.
            // Order matters: stop the dashboard first (leaves the alternate screen
            // and shows the cursor), then shut the modules down.
            dashboard?.Stop();
            orchestrator.Shutdown(saveConfig: true);
        }

        Console.ResetColor();
        Console.WriteLine();
        if (fatalError == null)
        {
            Console.WriteLine("Session saved. Goodbye.");
        }
        else
        {
            Console.WriteLine("The program stopped after an unexpected error:");
            Console.WriteLine($"  {fatalError.GetType().Name}: {fatalError.Message}");
            Console.WriteLine("The session was saved. See the log file for the full details.");
        }
    }

    /// <summary>
    /// Asks the user whether to start in simulation mode. Accepts Y/N and
    /// YES/NO. Retries until the user gives a recognizable answer.
    /// </summary>
    /// <returns><c>true</c> if the user chose simulation mode.</returns>
    private static bool AskSimulationMode()
    {
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine(" M32 Live Console Tool");
        Console.WriteLine("=================================================================");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Start in SIMULATION mode (no physical console)? [Y/N]: ");
            string? line = Console.ReadLine();
            if (line == null)
            {
                return false;
            }
            string answer = line.Trim().ToUpperInvariant();
            if (answer == "Y" || answer == "YES")
            {
                return true;
            }
            if (answer == "N" || answer == "NO")
            {
                return false;
            }
            Console.WriteLine("Unrecognized answer. Please answer Y or N.");
        }
    }

    /// <summary>
    /// Ctrl+C handler. Neutralizes the OS default behavior (which would kill the
    /// process abruptly) and triggers a clean shutdown. Ends with
    /// <see cref="Environment.Exit"/> so the process exits cleanly after the
    /// shutdown.
    /// </summary>
    /// <remarks>
    /// Without this handler, Ctrl+C would close the process without going through
    /// the menu's and dashboard's shutdown code, leaving the terminal in an
    /// inconsistent state (cursor hidden, alternate screen active) and the session
    /// unsaved.
    /// </remarks>
    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        dashboard?.Stop();
        orchestrator?.Shutdown(saveConfig: true);
        Environment.Exit(0);
    }

    /// <summary>
    /// Handler for process exit by any route other than Ctrl+C (closing the
    /// terminal window, kill, natural end of Main). Calls the orchestrator's
    /// shutdown to guarantee the session is saved and resources are released.
    /// Since <see cref="AppOrchestrator.Shutdown"/> is idempotent, it does not
    /// matter if it was already called from Ctrl+C or from the menu.
    /// </summary>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        dashboard?.Stop();
        orchestrator?.Shutdown(saveConfig: true);
    }
}
