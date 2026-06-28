using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LeBot.Application.Telemetry;

/// <summary>
/// Turns a Telegram chat id into a short, stable pseudonym for telemetry. The bot's target group is
/// private deployment identity (CLAUDE.md §Secrets), so the journal stores this digest in place of the
/// raw id — data minimisation, not anonymity. Be honest about the limit: a Telegram chat id is
/// low-entropy and this is an <i>unkeyed</i> SHA-256, so anyone holding the database could confirm a
/// <i>guessed</i> id by re-hashing it. The journal file is therefore local-secret — the same trust level
/// as the logs, which already record the raw chat id at Information. If the DB is ever exported off the
/// host, upgrade this to an HMAC keyed with a per-deployment secret (see ADR 0004). The digest is
/// deterministic (no salt) on purpose: the same chat must hash identically across restarts for the
/// per-chat rollups to add up.
/// </summary>
public static class ChatHasher
{
    /// <summary>Returns a 12-hex-char lower-case digest of <paramref name="chatId"/>.</summary>
    public static string Of(long chatId)
    {
        Span<byte> source = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(source, chatId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(source, digest);

        // 6 bytes → 12 hex chars: ample to keep collisions negligible at a personal bot's chat count,
        // short enough to read in a dashboard column.
        return Convert.ToHexStringLower(digest[..6]);
    }
}
