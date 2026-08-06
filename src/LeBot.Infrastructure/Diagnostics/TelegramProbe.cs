using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace LeBot.Infrastructure.Diagnostics;

/// <summary>
/// The <c>--doctor</c> check that the configured token actually talks to Telegram, via <c>getMe</c>.
/// Deliberately reports only reachability — never the bot's handle or id — so the checklist stays free
/// of deployment identity even when pasted into an issue.
/// </summary>
public static class TelegramProbe
{
    public static async Task<DoctorCheck> CheckAsync(string botToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = new TelegramBotClient(botToken);
            await client.GetMe(cancellationToken);
            return DoctorCheck.Pass("Telegram API", "reachable (getMe OK)");
        }
        catch (OperationCanceledException)
        {
            return DoctorCheck.Fail("Telegram API", "getMe timed out — check the network/firewall");
        }
        catch (ArgumentException ex)
        {
            return DoctorCheck.Fail("Telegram API", $"token is malformed: {ex.Message}");
        }
        catch (ApiRequestException ex)
        {
            return DoctorCheck.Fail(
                "Telegram API",
                $"getMe rejected (code {ex.ErrorCode}): {ex.Message} — revoke and reset the token if this persists");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Fail("Telegram API", $"getMe failed: {ex.Message}");
        }
    }
}
