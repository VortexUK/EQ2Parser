namespace EQ2Parser.Core.Analysis;

/// <summary>
/// The post-fight hold for the mini parse meters: visible whenever combat
/// runs, and for <c>holdSeconds</c> after it ends; hidden once the hold
/// expires (and before the session's first fight). 0 = never hide — the
/// pre-feature behaviour where the last fight lingers indefinitely.
/// Clock passed in per call so the timing is testable.
/// </summary>
public sealed class OverlayFadeGate
{
    private DateTimeOffset _lastActive = DateTimeOffset.MinValue;

    /// <summary>Call every tick with whether combat is live NOW.</summary>
    public bool ShouldHide(bool active, int holdSeconds, DateTimeOffset now)
    {
        if (active)
            _lastActive = now;
        if (holdSeconds <= 0)
            return false;
        return !active && now - _lastActive > TimeSpan.FromSeconds(holdSeconds);
    }
}
