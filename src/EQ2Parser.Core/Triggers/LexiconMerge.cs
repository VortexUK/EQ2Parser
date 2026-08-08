namespace EQ2Parser.Core.Triggers;

/// <summary>
/// The lexicon-pack merge semantics shared by the trigger and spell-timer
/// stores. These two MUST agree — the sync service persists one override
/// file per kind and expects identical behavior — and they were previously
/// duplicated conventions in two App services:
///   * a pack row whose key collides with a USER-owned row is dropped
///     (the user's copy/fork always wins);
///   * the user's sticky enable/disable overrides re-apply in BOTH
///     directions (disabling a curator-enabled row sticks, and enabling a
///     curator-disabled row sticks — only storing disables meant the
///     latter reverted on every sync).
/// </summary>
public static class LexiconMerge
{
    /// <summary>The rows that actually enter the store, with the user's
    /// overrides applied. Unmodified rows pass through by reference.</summary>
    public static List<T> Plan<T>(
        IEnumerable<T> pack,
        IReadOnlySet<string> customKeys,
        IReadOnlySet<string> disabledKeys,
        IReadOnlySet<string> enabledKeys,
        Func<T, string> keyOf,
        Func<T, bool> enabledOf,
        Func<T, bool, T> withEnabled)
    {
        List<T> planned = [];
        foreach (var item in pack)
        {
            var key = keyOf(item);
            if (customKeys.Contains(key))
                continue;
            var enabled = !disabledKeys.Contains(key) && (enabledOf(item) || enabledKeys.Contains(key));
            planned.Add(enabled == enabledOf(item) ? item : withEnabled(item, enabled));
        }
        return planned;
    }
}
