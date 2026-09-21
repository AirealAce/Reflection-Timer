using System.Threading.Channels;
using NAudio.Wave;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class AudioPreviewTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        foreach (var delay in new[] { 2, 5, 10 }) {
            var clock = new Clock(); var backend = new Backend();
            using var player = new AlertSoundPlayer(backend, timeProvider: clock);
            var task = player.PlayAsync("preview.mp3", 80, SoundBehavior.Polite, SoundEvent.LowTime,
                preview: true, fadeOutAfterSeconds: delay, soundVolume: 50);
            var playback = await backend.Next();
            clock.Advance(TimeSpan.FromSeconds(20)); // Includes arbitrary decoder/device startup time.
            check(!playback.Token.IsCancellationRequested && !task.IsCompleted,
                $"A {delay}-second preview fade is not cut off by the old five-second wall-clock limit");
            var provider = new LiveGainProvider(new Ones(), playback.Level);
            var before = new float[delay * 2000];
            check(provider.Read(before, 0, before.Length) == before.Length && before.All(x => x == .4f),
                $"Preview retains App sound and event volume for its full {delay}-second fade delay");
            var tail = new float[4000]; var read = provider.Read(tail, 0, tail.Length);
            check(read == 2000 && tail[0] == .4f && tail[1] == .4f && Math.Abs(tail[1000] - .2f) < .001f && tail[read - 1] < .001f && provider.Read(tail, 0, tail.Length) == 0,
                $"Preview applies the complete one-second stereo fade and then ends: delay {delay}");
            playback.Complete.TrySetResult();
            check(await task == AlertSoundResult.Played, "A completed fade finishes the preview normally");
        }

        var plainClock = new Clock(); var plainBackend = new Backend();
        using (var player = new AlertSoundPlayer(plainBackend, timeProvider: plainClock)) {
            var task = player.PlayAsync("preview.mp3", 100, SoundBehavior.Polite, SoundEvent.Success, preview: true);
            var playback = await plainBackend.Next();
            plainClock.Advance(TimeSpan.FromSeconds(4));
            check(!playback.Token.IsCancellationRequested, "A preview with fading disabled plays before the five-second limit");
            plainClock.Advance(TimeSpan.FromSeconds(1));
            check(await task.WaitAsync(TimeSpan.FromSeconds(4)) == AlertSoundResult.PreviewFinished,
                "A preview with fading disabled still stops at five seconds");
            task = player.PlayAsync("real.mp3", 100, SoundBehavior.Polite, SoundEvent.Success);
            playback = await plainBackend.Next(); plainClock.Advance(TimeSpan.FromSeconds(60));
            check(!playback.Token.IsCancellationRequested, "Real session audio retains its normal playback duration");
            playback.Complete.TrySetResult(); await task;
        }

        var controlBackend = new Backend();
        using (var player = new AlertSoundPlayer(controlBackend)) {
            var firstTask = player.PlayAsync("first.mp3", 100, SoundBehavior.Polite, SoundEvent.LowTime, preview: true, fadeOutAfterSeconds: 10);
            var first = await controlBackend.Next();
            var nextTask = player.PlayAsync("next.mp3", 100, SoundBehavior.Polite, SoundEvent.LowTime, preview: true, fadeOutAfterSeconds: 12);
            var next = await controlBackend.Next();
            check(await firstTask == AlertSoundResult.Cancelled && first.Token.IsCancellationRequested,
                "Starting another preview still cancels a preview waiting for its fade");
            player.Stop();
            check(await nextTask == AlertSoundResult.Cancelled && next.Token.IsCancellationRequested,
                "Stop all app audio can immediately stop an extended fading preview");
            var fallbackTask = player.PlayAsync("missing.mp3", 100, SoundBehavior.Polite, SoundEvent.LowTime,
                fallback: "fallback.mp3", preview: true, fadeOutAfterSeconds: 10);
            var missing = await controlBackend.Next(); missing.Complete.TrySetException(new IOException("Synthetic decode failure"));
            var fallback = await controlBackend.Next();
            check(fallback.Level.FadeOutAfterSeconds == 10 && fallback.Level.RequestedFadeSeconds == 0,
                "An unavailable preview track's fallback keeps timed fading without simulating message delivery");
            // A short source naturally ends before the configured delay, without being looped or padded.
            var shortSource = new LiveGainProvider(new Ones(1000), fallback.Level);
            var buffer = new float[4000];
            check(shortSource.Read(buffer, 0, buffer.Length) == 1000 && shortSource.Read(buffer, 0, buffer.Length) == 0,
                "Preview allows a short track to finish naturally before its fade delay");
            fallback.Complete.TrySetResult();
            check(await fallbackTask == AlertSoundResult.DefaultFallback, "Fading preview fallback finishes normally");
        }

        foreach (var kind in Enum.GetValues<SoundEvent>()) {
            var engine = new TimerEngine(new MemoryStore { State = new() { LoggingEnabled = false } });
            engine.SetSound(kind, new() { FadeOutEnabled = true, FadeOutAfterSeconds = 12, FadeOutAfterMessageSent = true, Behavior = SoundBehavior.Polite });
            var backend = new Backend();
            using var services = new PreviewServices(engine, Path.Combine(Path.GetTempPath(), "ReflectionTimer-Preview-" + Guid.NewGuid().ToString("N")), audio: backend);
            var task = services.Play(kind, preview: true);
            var playback = await backend.Next();
            check(playback.Level.FadeOutAfterSeconds == 12 && playback.Level.RequestedFadeSeconds == 0,
                "Preview uses the saved timed fade without triggering message-sent fading: " + kind);
            playback.Complete.TrySetResult(); await task;
            engine.SetSound(kind, AudioSettings.From(engine.Snapshot).For(kind) with { FadeOutEnabled = false });
            task = services.Play(kind, preview: true); playback = await backend.Next();
            check(playback.Level.FadeOutAfterSeconds is null, "Disabling timed fading removes it from preview: " + kind);
            playback.Complete.TrySetResult(); await task;
        }
    }

    private sealed class Ones(int remaining = int.MaxValue) : ISampleProvider
    {
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(1000, 2);
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer) { var count = Math.Min(buffer.Length, remaining); buffer[..count].Fill(1f); remaining -= count; return count; }
    }
    private sealed record Playback(AudioLevel Level, CancellationToken Token)
    {
        public TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Backend : IAlertAudioBackend
    {
        private readonly Channel<Playback> started = Channel.CreateUnbounded<Playback>();
        public async Task PlayAsync(string path, AudioLevel level, CancellationToken token)
        {
            var playback = new Playback(level, token); started.Writer.TryWrite(playback);
            await playback.Complete.Task.WaitAsync(token);
        }
        public async Task<Playback> Next() => await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
    }
    private sealed class Clock : TimeProvider
    {
        private readonly List<ClockTimer> timers = [];
        public TimeSpan Elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state); timer.Change(dueTime, period);
            timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan time) { Elapsed += time; foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Elapsed + dueTime; return true; }
            public void Fire() { if (due is { } at && at <= clock.Elapsed) { due = null; callback(state); } }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
