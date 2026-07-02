using System.Buffers.Binary;
using LeBot.Application.Telemetry;

namespace LeBot.Application.Tests.Telemetry;

public class ChatHasherTests
{
    [Fact]
    public void Of_SameChatId_IsStableAcrossCalls()
    {
        // Stability is the whole point: per-chat rollups only add up if a chat hashes identically
        // every time, including across process restarts.
        ChatHasher.Of(-1001234567890).Should().Be(ChatHasher.Of(-1001234567890));
    }

    [Fact]
    public void Of_DifferentChatIds_ProduceDifferentHashes()
    {
        ChatHasher.Of(123).Should().NotBe(ChatHasher.Of(124));
    }

    [Fact]
    public void Of_Produces12LowerCaseHexChars()
    {
        ChatHasher.Of(42).Should().MatchRegex("^[0-9a-f]{12}$");
    }

    [Fact]
    public void Of_IsNotAReversibleEncodingOfTheChatId()
    {
        const long chatId = -1009998887776;

        var digest = ChatHasher.Of(chatId);

        // A real one-way hash must not just be a verbatim hex encoding of the id's bytes, and flipping
        // the id must avalanche the digest. Both assertions fail if Of regressed to leaking the raw
        // bytes — unlike the old "12 hex chars don't contain a 13-char decimal", which could never fail.
        Span<byte> raw = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(raw, chatId);
        digest.Should().NotBe(Convert.ToHexStringLower(raw)[..12]);
        ChatHasher.Of(chatId).Should().NotBe(ChatHasher.Of(chatId + 1));
    }
}
