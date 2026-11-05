namespace LeBot.Application.Telemetry;

/// <summary>How many links one person reposted from one host in a chat — the raw row behind <see cref="Leaderboard"/>.</summary>
/// <param name="SenderId">Telegram user id of the person who posted the links.</param>
/// <param name="Host">The links' lower-cased host, as journalled.</param>
/// <param name="Reposts">Successful reposts (media or text) from that host.</param>
public sealed record SenderHostCount(long SenderId, string Host, int Reposts);
