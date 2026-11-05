using System.Diagnostics;

namespace AionTranslator.UI;

public sealed record IncomingChat(long Id, string Speaker, string Text, string Channel);

public interface IChatSource
{
    event Action<IncomingChat>? Message;
    event Action<string>? Note;
    string Status { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
