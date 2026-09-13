using NAudio.Wave;
using ReflectionTimer.Core;

namespace ReflectionTimer.Desktop;

public enum AlertSoundResult { Played, DefaultFallback, Muted, Cancelled, Failed, PreviewFinished }

public interface IAlertAudioBackend
{
    Task PlayAsync(string path, AudioLevel level, CancellationToken cancellationToken);
}

public sealed class AudioLevel(int volume, int? fadeOutAfterSeconds = null, int soundVolume = 100)
{
    private int appVolume = Math.Clamp(volume, 0, 100), eventVolume = Math.Clamp(soundVolume, 0, 100);
    public int Volume => Volatile.Read(ref appVolume);
    public int SoundVolume => Volatile.Read(ref eventVolume);
    public int? FadeOutAfterSeconds { get; } = fadeOutAfterSeconds;
    private float multiplier = 1;
    private int requestedFadeSeconds;
    internal int RequestedFadeSeconds => Volatile.Read(ref requestedFadeSeconds);
    internal void RequestFadeOut(int seconds) => Interlocked.CompareExchange(ref requestedFadeSeconds, seconds, 0);
    public float Gain => Volume / 100f * (SoundVolume / 100f) * Volatile.Read(ref multiplier);
    internal void SetVolumes(int app, int sound) { Volatile.Write(ref appVolume, Math.Clamp(app, 0, 100)); Volatile.Write(ref eventVolume, Math.Clamp(sound, 0, 100)); }
    internal void Duck(bool ducked) => Volatile.Write(ref multiplier, ducked ? .25f : 1f);
}

// Volume changes are read by the audio thread, without touching Windows' mixer
// or device-wide volume. Other apps are never captured, stopped, or attenuated.
internal sealed class LiveGainProvider(ISampleProvider source, AudioLevel level) : ISampleProvider
{
    private long samplesPlayed;
    private long? requestedFadeStartFrame;
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<float> buffer)
    {
        var fadeStartFrame = level.FadeOutAfterSeconds is { } seconds ? (long)seconds * WaveFormat.SampleRate : (long?)null;
        var requestedSeconds = level.RequestedFadeSeconds;
        if (requestedSeconds > 0) requestedFadeStartFrame ??= samplesPlayed / WaveFormat.Channels;
        var requestedFrames = (long)requestedSeconds * WaveFormat.SampleRate;
        long? endFrame = fadeStartFrame + WaveFormat.SampleRate;
        if (requestedFadeStartFrame is { } requestedStart)
            endFrame = Math.Min(endFrame ?? long.MaxValue, requestedStart + requestedFrames);
        if (endFrame is { } end) {
            // Count audio frames, not wall time: decoding/device setup cannot
            // consume a delay or fade. Every channel shares the same envelope.
            var remaining = end * WaveFormat.Channels - samplesPlayed;
            if (remaining <= 0) return 0;
            buffer = buffer[..(int)Math.Min(buffer.Length, remaining)];
        }
        var read = source.Read(buffer); var gain = level.Gain;
        for (var i = 0; i < read; i++) {
            var fade = fadeStartFrame is { } fadeStart
                ? Math.Clamp(1f - ((samplesPlayed + i) / WaveFormat.Channels - fadeStart) / (float)WaveFormat.SampleRate, 0f, 1f)
                : 1f;
            if (requestedFadeStartFrame is { } start)
                fade = Math.Min(fade, Math.Clamp(1f - ((samplesPlayed + i) / WaveFormat.Channels - start) / (float)requestedFrames, 0f, 1f));
            buffer[i] *= gain * fade;
        }
        samplesPlayed += read;
        return read;
    }
}

public sealed class Mp3AudioBackend : IAlertAudioBackend
{
    public static string ValidateCustomFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a local MP3 file.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new ArgumentException("That MP3 file is no longer available. Choose it again.");
        if (new FileInfo(fullPath).Length is <= 0 or > 50 * 1024 * 1024)
            throw new ArgumentException("Choose a non-empty MP3 file no larger than 50 MB.");
        try {
            using var reader = new AudioFileReader(fullPath);
            if (reader.TotalTime <= TimeSpan.Zero || reader.Read(new byte[4096], 0, 4096) == 0)
                throw new InvalidDataException();
        } catch (Exception) { throw new ArgumentException("That file could not be decoded as audio. Choose another MP3."); }
        return fullPath;
    }

    public async Task PlayAsync(string path, AudioLevel level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builtIn = path.StartsWith("builtin:", StringComparison.Ordinal);
        using var reader = builtIn ? null : new AudioFileReader(path);
        ISampleProvider source = reader is not null ? reader : new BuiltInTone(Enum.Parse<SoundEvent>(path[8..]));
        using var output = new WaveOut();
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => stopped.TrySetResult(e.Exception);
        output.Init(new LiveGainProvider(source, level));
        cancellationToken.ThrowIfCancellationRequested();
        output.Play();
        using var registration = cancellationToken.Register(() => {
            try { output.Stop(); }
            catch (Exception error) { stopped.TrySetResult(error); }
        });
        // Wait for the audio thread before disposing the decoder/device, even
        // after Stop. Neither playback nor decoding blocks the timer's UI thread.
        var failure = await stopped.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null) throw new IOException("Audio playback failed.", failure);
    }
}

public sealed class AlertSoundPlayer : IDisposable
{
    public static string BundledPath => File.Exists(Path.Combine(AppContext.BaseDirectory, "popup.mp3"))
        ? Path.Combine(AppContext.BaseDirectory, "popup.mp3") : BuiltInTone.PathFor(SoundEvent.SessionEnd);
    private readonly IAlertAudioBackend backend;
    private readonly string defaultPath;
    private readonly TimeProvider timeProvider;
    public static TimeSpan PreviewDuration => TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly List<Voice> voices = [];
    private Task stopping = Task.CompletedTask;
    private bool disposed;

    private sealed class Voice(int volume, SoundBehavior behavior, SoundEvent kind, bool preview, int? fadeOutAfterSeconds, int soundVolume, Guid? sessionId)
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly AudioLevel Level = new(volume, fadeOutAfterSeconds, soundVolume);
        public readonly SoundBehavior Behavior = behavior;
        public readonly SoundEvent Kind = kind;
        public readonly bool Preview = preview;
        public readonly Guid? SessionId = sessionId;
        public bool Running;
    }

    public AlertSoundPlayer(IAlertAudioBackend? backend = null, string? defaultPath = null, TimeProvider? timeProvider = null)
    {
        this.backend = backend ?? new Mp3AudioBackend();
        this.defaultPath = defaultPath ?? BundledPath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AlertSoundResult> PlayAsync(string customPath, int volume) =>
        PlayAsync(customPath, volume, SoundBehavior.Disruptive, SoundEvent.SessionEnd);

    public Task<AlertSoundResult> PlayAsync(string? path, int volume, SoundBehavior behavior, SoundEvent kind, string? fallback = null, bool preview = false, int? fadeOutAfterSeconds = null, int soundVolume = 100, Guid? sessionId = null)
    {
        if (fadeOutAfterSeconds is < 1 or > TimerEngine.MaxDuration) throw new ArgumentOutOfRangeException(nameof(fadeOutAfterSeconds));
        lock (gate) {
            if (disposed) return Task.FromResult(AlertSoundResult.Cancelled);
            // Even None replaces the previous preview, but never stops a real
            // session sound or falls back to a bundled file.
            if (preview) CancelVoices(voices.Where(x => x.Preview).ToArray());
            if (path is null) return Task.FromResult(AlertSoundResult.Muted);
            if (volume <= 0 || soundVolume <= 0) return Task.FromResult(AlertSoundResult.Muted); // Muted events cannot interrupt audible ones.
            if (!Enum.IsDefined(behavior)) behavior = SoundBehavior.Disruptive;
            if (behavior == SoundBehavior.Disruptive) CancelVoices(voices.ToArray());
            if (voices.Count >= 32) return Task.FromResult(AlertSoundResult.Cancelled);
            var request = new Voice(volume, behavior, kind, preview, fadeOutAfterSeconds, soundVolume, sessionId); voices.Add(request);
            var waitForStops = stopping;
            return Task.Run(() => RunAsync(path, fallback ?? defaultPath, request, waitForStops));
        }
    }

    private async Task<AlertSoundResult> RunAsync(string path, string fallback, Voice request, Task waitForStops)
    {
        using var limit = request.Preview ? new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider) : null;
        using var linked = limit is null ? null : CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token, limit.Token);
        var token = linked?.Token ?? request.Cancellation.Token;
        try {
            await waitForStops.WaitAsync(token).ConfigureAwait(false);
            lock (gate) { token.ThrowIfCancellationRequested(); request.Running = true; UpdateGains(); }
            limit?.CancelAfter(PreviewDuration); // One budget shared by the selected file and any fallback.
            if (!string.IsNullOrEmpty(path)) {
                try {
                    await backend.PlayAsync(path, request.Level, token).ConfigureAwait(false);
                    return AlertSoundResult.Played;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) {
                    token.ThrowIfCancellationRequested();
                    await backend.PlayAsync(fallback, request.Level, token).ConfigureAwait(false);
                    return AlertSoundResult.DefaultFallback;
                }
            }
            await backend.PlayAsync(fallback, request.Level, token).ConfigureAwait(false);
            return AlertSoundResult.Played;
        }
        catch (OperationCanceledException) { return limit?.IsCancellationRequested == true && !request.Cancellation.IsCancellationRequested
            ? AlertSoundResult.PreviewFinished : AlertSoundResult.Cancelled; }
        catch (Exception) { return AlertSoundResult.Failed; }
        finally {
            lock (gate) {
                voices.Remove(request); UpdateGains(); request.Cancellation.Dispose(); request.Done.TrySetResult();
            }
        }
    }

    private void UpdateGains()
    {
        var foreground = voices.LastOrDefault(x => x.Running && !x.Cancellation.IsCancellationRequested && x.Behavior == SoundBehavior.Assertive
            && x.Level.Volume > 0 && x.Level.SoundVolume > 0);
        foreach (var voice in voices) voice.Level.Duck(foreground is not null && !ReferenceEquals(voice, foreground));
    }
    public void UpdateVolumes(int appVolume, AudioSettings settings)
    {
        lock (gate) {
            foreach (var voice in voices) voice.Level.SetVolumes(appVolume, settings.For(voice.Kind).Volume);
            UpdateGains();
        }
    }
    private void CancelVoices(Voice[] targets)
    {
        foreach (var voice in targets) voice.Cancellation.Cancel();
        stopping = Task.WhenAll(targets.Select(x => x.Done.Task).Append(stopping));
        UpdateGains();
    }
    public void Stop(SoundEvent? kind = null) { lock (gate) CancelVoices(voices.Where(x => kind is null || x.Kind == kind).ToArray()); }
    public void FadeOut(SoundEvent kind, Guid sessionId, int seconds)
    {
        TimerEngine.ValidateDuration(seconds);
        lock (gate) {
            foreach (var voice in voices.Where(x => x.Kind == kind && x.SessionId == sessionId && !x.Preview && !x.Cancellation.IsCancellationRequested))
                voice.Level.RequestFadeOut(seconds);
        }
    }
    public void Dispose() { lock (gate) { disposed = true; CancelVoices(voices.ToArray()); } }
}
