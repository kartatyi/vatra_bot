using LeBot.Domain.Media;

namespace LeBot.Application.Caching;

/// <summary>
/// A repost served straight from <see cref="Ports.IMediaCache"/>: the stored payload — its items
/// pointing at the cache's own copies and flagged <see cref="MediaPayload.RetainFiles"/> — plus the
/// name of the extractor that originally produced it, so metrics and logs read the same on a cache
/// hit as they do on a fresh extraction.
/// </summary>
/// <param name="Payload">The payload to send, ready to hand to the messenger unchanged.</param>
/// <param name="Extractor">Type name of the extractor that produced the payload the first time.</param>
public sealed record CachedRepost(MediaPayload Payload, string Extractor);
