namespace ReflectionTimer.Accessible;

// One monotonic deadline, independent of the session clock and wall-clock edits.
// Re-arming replaces pending work; hiding/disabling cancels it, with no task backlog.
internal sealed class ViewerAutoHideDeadline(Func<long>? milliseconds = null)
{
    private readonly Func<long> now = milliseconds ?? (() => Environment.TickCount64);
    private long? deadline;
    private long? startedAt;
    internal void Cancel() { deadline = null; startedAt = null; }
    internal void Restart(int seconds) { startedAt = now(); ChangeDelay(seconds); }
    internal void ChangeDelay(int seconds) { if (startedAt is { } at) deadline = at + (long)seconds * 1000; }
    internal int? RemainingMilliseconds => deadline is { } end
        ? (int)Math.Clamp(end - now(), 0, int.MaxValue) : null;
}
