using System.Speech.Synthesis;
using Ashok.Core.Speech;

namespace Ashok.Speech;

/// <summary>Text-to-speech through Windows' built-in voices. Local and free.</summary>
public sealed class WindowsSpeaker : ISpeaker, IDisposable
{
    // Slightly slower than the default; the first users are older adults.
    private const int SlowRate = -1;

    private readonly SpeechSynthesizer _synth = new() { Rate = SlowRate };

    public bool HasVoice => _synth.GetInstalledVoices().Any(v => v.Enabled);

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text) || !HasVoice) return Task.CompletedTask;

        var prompt = new Prompt(text);
        var done = new TaskCompletionSource();

        // Cancelling the previous utterance also raises SpeakCompleted, so match on this prompt only.
        void OnCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            if (!ReferenceEquals(e.Prompt, prompt)) return;
            _synth.SpeakCompleted -= OnCompleted;
            done.TrySetResult();
        }

        _synth.SpeakCompleted += OnCompleted;
        cancellationToken.Register(Stop);
        _synth.SpeakAsync(prompt);
        return done.Task;
    }

    public void Stop() => _synth.SpeakAsyncCancelAll();

    /// <summary>Synthesizes to an in-memory WAV without playing it. Used to check a voice works.</summary>
    public byte[] RenderToWave(string text)
    {
        using var synth = new SpeechSynthesizer { Rate = SlowRate };
        using var stream = new MemoryStream();
        synth.SetOutputToWaveStream(stream);
        synth.Speak(text);
        return stream.ToArray();
    }

    public void Dispose() => _synth.Dispose();
}
