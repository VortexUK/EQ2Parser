namespace EQ2Parser.App.Services;

/// <summary>
/// App-wide Ctrl+Z stack for destructive actions (deleted triggers, timers,
/// fights). Each entry is a closure that restores what was destroyed. UI
/// thread only — deletes and undos both happen in command handlers.
/// </summary>
public sealed class UndoService
{
    private const int Capacity = 20;

    private readonly List<Action> _stack = [];

    public void Push(Action restore)
    {
        _stack.Add(restore);
        if (_stack.Count > Capacity)
            _stack.RemoveAt(0);
    }

    /// <summary>Pop and run the newest restore. False when empty.</summary>
    public bool TryUndo()
    {
        if (_stack.Count == 0)
            return false;
        var restore = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        restore();
        return true;
    }
}
