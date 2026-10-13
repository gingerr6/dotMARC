using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne refused the saved sign-in (or there isn't one), so someone has to reconnect. A network fault or
/// server error is an ordinary HttpRequestException instead, and is worth retrying.</summary>
public sealed class NinjaOneConnectionLostException(string message) : Exception(message);

public sealed record NinjaOneTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null);

/// <summary>NinjaOne's OAuth endpoints: /ws/oauth/authorize to sign in and /ws/oauth/token for both grants.</summary>
public static class NinjaOneOAuth
{
    public static string AuthorizeUrl(NinjaOneSettings settings, string redirectUri, string state) =>
        $"https://{settings.Instance}/ws/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(settings.ClientId!)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(NinjaOneOptions.Scope)}&state={Uri.EscapeDataString(state)}";

    /// <summary>A 400 or 401 is NinjaOne refusing the grant, so it throws <see cref="NinjaOneConnectionLostException"/>
    /// with NinjaOne's reason. NinjaOne's error bodies never echo the secret or the token.</summary>
    public static async Task<NinjaOneTokenResponse> RequestTokenAsync(HttpClient httpClient, string instance, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{instance}/ws/oauth/token") { Content = new FormUrlEncodedContent(form) };
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new NinjaOneConnectionLostException(await DescribeRefusalAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"NinjaOne's sign-in service returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<NinjaOneTokenResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("NinjaOne's sign-in service returned an empty response.");
    }

    private static async Task<string> DescribeRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var summary = $"NinjaOne refused the sign-in ({(int)response.StatusCode})";
        try
        {
            var refusal = await response.Content.ReadFromJsonAsync<TokenRefusal>(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refusal?.Error))
            {
                return string.IsNullOrWhiteSpace(refusal.Description) ? $"{summary}: {refusal.Error}" : $"{summary}: {refusal.Error} - {refusal.Description}";
            }
        }
        catch (JsonException)
        {
            // Not an OAuth error body, so the status code will have to do.
        }

        return summary;
    }

    private sealed record TokenRefusal(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}
