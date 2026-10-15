using LeBot.Domain.Media;

namespace LeBot.Domain.Tests.Media;

public class DeliveryErrorTests
{
    [Fact]
    public void TelegramRefused_Reason_IncludesCodeAndDetail()
    {
        var error = new DeliveryError.TelegramRefused(400, "Bad Request: file is too big");

        error.Reason.Should().Contain("400");
        error.Reason.Should().Contain("Bad Request: file is too big");
    }

    [Fact]
    public void TelegramUnreachable_Reason_IncludesDetail()
    {
        var error = new DeliveryError.TelegramUnreachable("connection reset");

        error.Reason.Should().Contain("connection reset");
    }
}
