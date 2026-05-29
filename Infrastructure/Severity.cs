namespace M32LiveConsoleTool.Infrastructure;

/// <summary>
/// Severity level of an event recorded by <see cref="ShowLogger"/>.
/// </summary>
/// <remarks>
/// Classifies show-log entries: Info for routine corrections, Warning for
/// recoverable situations (for example clipping detected and attenuated), and
/// Error for serious failures such as losing the OSC connection to the console.
/// </remarks>
public enum Severity
{
    Info,
    Warning,
    Error
}
