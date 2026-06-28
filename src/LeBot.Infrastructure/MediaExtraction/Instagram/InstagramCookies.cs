namespace LeBot.Infrastructure.MediaExtraction.Instagram;

/// <summary>
/// The Instagram session cookies the private web API needs. <see cref="SessionId"/> is the only one
/// strictly required; <see cref="DsUserId"/> and <see cref="CsrfToken"/> are sent when present
/// because Instagram occasionally cross-checks them.
/// </summary>
internal sealed record InstagramCookies(string SessionId, string? DsUserId, string? CsrfToken)
{
    /// <summary>Renders the cookies into a <c>Cookie:</c> request-header value.</summary>
    public string ToHeaderValue()
    {
        var parts = new List<string>(3) { $"sessionid={SessionId}" };
        if (!string.IsNullOrEmpty(DsUserId))
        {
            parts.Add($"ds_user_id={DsUserId}");
        }

        if (!string.IsNullOrEmpty(CsrfToken))
        {
            parts.Add($"csrftoken={CsrfToken}");
        }

        return string.Join("; ", parts);
    }
}
