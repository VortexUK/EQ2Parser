namespace EQ2Parser.Core.Triggers;

/// <summary>A trigger share seen in live chat: who pasted it, whether it
/// was the log owner's own paste, and the parsed trigger.</summary>
public sealed record SharedTrigger(string Sharer, bool Self, Trigger Trigger);

/// <summary>
/// Detects ACT trigger-share snippets pasted into EQ2 chat — the community
/// convention for handing triggers around mid-raid. The chat line carries
/// the literal share XML:
/// <c>\aPC -1 Martyn:Martyn\/a says to the raid party, "&lt;Trigger R="..." SD="..." .../&gt;"</c>
/// Only player chat counts (PC links or the owner's own "You say/tell") —
/// an NPC "saying" trigger XML is not a thing and stays ignored.
/// </summary>
public static class ChatTriggerShare
{
    /// <summary>Parse a share out of a chat message, or null when the line
    /// isn't a player-chat share. Cheap early-outs — runs per log line
    /// behind a Contains guard.</summary>
    public static SharedTrigger? TryExtract(string message)
    {
        var start = message.IndexOf("<Trigger", StringComparison.Ordinal);
        if (start < 0)
            return null;
        var end = message.IndexOf("/>", start, StringComparison.Ordinal);
        if (end < 0)
            return null;

        string sharer;
        bool self;
        if (message.StartsWith("You say", StringComparison.Ordinal)
            || message.StartsWith("You tell", StringComparison.Ordinal))
        {
            sharer = "You";
            self = true;
        }
        else if (message.StartsWith(@"\aPC ", StringComparison.Ordinal))
        {
            // \aPC -1 Martyn:Martyn\/a says ... — display name after ':'.
            var colon = message.IndexOf(':');
            var linkEnd = message.IndexOf(@"\/a ", StringComparison.Ordinal);
            if (colon < 0 || linkEnd < 0 || linkEnd < colon)
                return null;
            sharer = message[(colon + 1)..linkEnd];
            self = false;
        }
        else
        {
            return null; // NPC speech / non-chat line
        }

        if (ActShareFormat.TryImport(message[start..(end + 2)]) is not Trigger trigger)
            return null;
        return new SharedTrigger(sharer, self, trigger);
    }
}
