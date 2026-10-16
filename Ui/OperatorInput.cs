namespace M32LiveConsoleTool.Ui;

/// <summary>Every menu, form and notice uses the same emergency-key handler.</summary>
public sealed class OperatorInput(Action releaseAll)
{
    public ConsoleKeyInfo ReadKey() => Handle(Console.ReadKey(intercept: true));

    internal ConsoleKeyInfo Handle(ConsoleKeyInfo key)
    {
        if (key.Key != ConsoleKey.F12) return key;
        releaseAll();
        // Also abandon the current form; a half-entered action must not resume control.
        return new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false);
    }
}
