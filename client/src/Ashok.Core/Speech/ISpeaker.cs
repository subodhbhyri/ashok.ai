namespace Ashok.Core.Speech;

public interface ISpeaker
{
    /// <summary>Speaks the text, cancelling anything already being spoken.</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    void Stop();
}
