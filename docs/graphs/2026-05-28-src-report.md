# Graph Report - src/  (2026-05-28)

## Corpus Check
- Corpus is ~5,055 words - fits in a single context window. You may not need a graph.

## Summary
- 89 nodes · 113 edges · 19 communities detected
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Instagram Embed Extractor|Instagram Embed Extractor]]
- [[_COMMUNITY_yt-dlp Extractor|yt-dlp Extractor]]
- [[_COMMUNITY_Telegram Messenger|Telegram Messenger]]
- [[_COMMUNITY_DI Composition|DI Composition]]
- [[_COMMUNITY_Handler Use Case|Handler Use Case]]
- [[_COMMUNITY_Update Dispatcher|Update Dispatcher]]
- [[_COMMUNITY_URL Extraction|URL Extraction]]
- [[_COMMUNITY_IPlatformExtractor Port|IPlatformExtractor Port]]
- [[_COMMUNITY_ITelegramMessenger Port|ITelegramMessenger Port]]
- [[_COMMUNITY_Result Type|Result Type]]
- [[_COMMUNITY_IUrlExtractor Port|IUrlExtractor Port]]
- [[_COMMUNITY_Telegram Options|Telegram Options]]
- [[_COMMUNITY_yt-dlp Options|yt-dlp Options]]
- [[_COMMUNITY_IncomingMessage Record|IncomingMessage Record]]
- [[_COMMUNITY_ExtractionError Records|ExtractionError Records]]
- [[_COMMUNITY_MediaItem Record|MediaItem Record]]
- [[_COMMUNITY_MediaKind Enum|MediaKind Enum]]
- [[_COMMUNITY_MediaPayload Record|MediaPayload Record]]
- [[_COMMUNITY_Host Entry Point|Host Entry Point]]

## God Nodes (most connected - your core abstractions)
1. `InstagramEmbedExtractor` - 19 edges
2. `YtDlpPlatformExtractor` - 12 edges
3. `TelegramBotMessenger` - 11 edges
4. `DependencyInjection` - 4 edges
5. `HandleIncomingMessageHandler` - 4 edges
6. `TelegramUpdateDispatcher` - 4 edges
7. `RegexUrlExtractor` - 4 edges
8. `IPlatformExtractor` - 3 edges
9. `ITelegramMessenger` - 3 edges
10. `IUrlExtractor` - 2 edges

## Surprising Connections (you probably didn't know these)
- `YtDlpPlatformExtractor` --inherits--> `IPlatformExtractor`  [EXTRACTED]
  src\LeBot.Infrastructure\MediaExtraction\YtDlp\YtDlpPlatformExtractor.cs →   _Bridges community 0 → community 1_

## Communities

### Community 0 - "Instagram Embed Extractor"
Cohesion: 0.18
Nodes (2): InstagramEmbedExtractor, IPlatformExtractor

### Community 1 - "yt-dlp Extractor"
Cohesion: 0.33
Nodes (1): YtDlpPlatformExtractor

### Community 2 - "Telegram Messenger"
Cohesion: 0.33
Nodes (2): ITelegramMessenger, TelegramBotMessenger

### Community 3 - "DI Composition"
Cohesion: 0.4
Nodes (1): DependencyInjection

### Community 4 - "Handler Use Case"
Cohesion: 0.6
Nodes (1): HandleIncomingMessageHandler

### Community 5 - "Update Dispatcher"
Cohesion: 0.5
Nodes (2): BackgroundService, TelegramUpdateDispatcher

### Community 6 - "URL Extraction"
Cohesion: 0.5
Nodes (2): IUrlExtractor, RegexUrlExtractor

### Community 7 - "IPlatformExtractor Port"
Cohesion: 0.5
Nodes (1): IPlatformExtractor

### Community 8 - "ITelegramMessenger Port"
Cohesion: 0.5
Nodes (1): ITelegramMessenger

### Community 9 - "Result Type"
Cohesion: 0.5
Nodes (0): 

### Community 10 - "IUrlExtractor Port"
Cohesion: 0.67
Nodes (1): IUrlExtractor

### Community 11 - "Telegram Options"
Cohesion: 1.0
Nodes (1): TelegramOptions

### Community 12 - "yt-dlp Options"
Cohesion: 1.0
Nodes (1): YtDlpOptions

### Community 13 - "IncomingMessage Record"
Cohesion: 1.0
Nodes (0): 

### Community 14 - "ExtractionError Records"
Cohesion: 1.0
Nodes (0): 

### Community 15 - "MediaItem Record"
Cohesion: 1.0
Nodes (0): 

### Community 16 - "MediaKind Enum"
Cohesion: 1.0
Nodes (0): 

### Community 17 - "MediaPayload Record"
Cohesion: 1.0
Nodes (0): 

### Community 18 - "Host Entry Point"
Cohesion: 1.0
Nodes (0): 

## Knowledge Gaps
- **2 isolated node(s):** `TelegramOptions`, `YtDlpOptions`
  These have ≤1 connection - possible missing edges or undocumented components.
- **Thin community `Telegram Options`** (2 nodes): `TelegramOptions.cs`, `TelegramOptions`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `yt-dlp Options`** (2 nodes): `YtDlpOptions.cs`, `YtDlpOptions`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `IncomingMessage Record`** (1 nodes): `IncomingMessage.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `ExtractionError Records`** (1 nodes): `ExtractionError.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `MediaItem Record`** (1 nodes): `MediaItem.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `MediaKind Enum`** (1 nodes): `MediaKind.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `MediaPayload Record`** (1 nodes): `MediaPayload.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Host Entry Point`** (1 nodes): `Program.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `YtDlpPlatformExtractor` connect `yt-dlp Extractor` to `Instagram Embed Extractor`?**
  _High betweenness centrality (0.066) - this node is a cross-community bridge._
- **What connects `TelegramOptions`, `YtDlpOptions` to the rest of the system?**
  _2 weakly-connected nodes found - possible documentation gaps or missing edges._