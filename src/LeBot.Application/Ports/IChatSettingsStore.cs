using LeBot.Domain.Settings;

namespace LeBot.Application.Ports;

/// <summary>Durable per-chat <see cref="ChatSettings"/>, changed through the <c>/settings</c> keyboard.</summary>
public interface IChatSettingsStore
{
    /// <summary>
    /// The chat's settings, or <see cref="ChatSettings.Default"/> when nobody has changed them.
    /// <b>Never throws on a storage failure:</b> a repost must not break because its settings
    /// couldn't be read, so the adapter logs and answers with the default.
    /// </summary>
    Task<ChatSettings> GetAsync(long chatId, CancellationToken cancellationToken);

    /// <summary>Replaces the chat's settings. Unlike <see cref="GetAsync"/>, a failure surfaces — the person changing them must not be told it worked.</summary>
    Task SaveAsync(long chatId, ChatSettings settings, CancellationToken cancellationToken);
}
