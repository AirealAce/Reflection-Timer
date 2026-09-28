namespace ReflectionTimer.Accessible;

// One monotonic deadline, independent of the session clock and wall-clock edits.
// Re-arming replaces pending work; hiding/disabling cancels it, with no task backlog.
internal sealed class ViewerAutoHideDeadline(Func<long>? milliseconds = null)
{
    private readonly Func<long> now = milliseconds ?? (() => Environment.TickCount64);
    private long? deadline;
    internal void Cancel() => deadline = null;
    internal void Restart(int seconds) => deadline = now() + (long)seconds * 1000;
    internal int? RemainingMilliseconds => deadline is { } end
        ? (int)Math.Clamp(end - now(), 0, int.MaxValue) : null;
}
