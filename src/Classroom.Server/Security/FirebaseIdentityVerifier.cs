using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blossom.Classroom.Server.Configuration;

namespace Blossom.Classroom.Server.Security;

public sealed record FirebaseIdentity(
    string Subject,
    string Email,
    string DisplayName,
    bool EmailVerified,
    string Provider);

/// <summary>
/// Verifies a Firebase ID token through Firebase Identity Toolkit.
/// The Web API key is a project identifier, not a server secret; the token is
/// still checked by Firebase before a local Classroom session is created.
/// </summary>
public sealed class FirebaseIdentityVerifier(
    HttpClient httpClient,
    ServerOptions options)
{
    public async Task<FirebaseIdentity?> VerifyAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (!options.FirebaseConfigured
            || string.IsNullOrWhiteSpace(idToken)
            || idToken.Length > 16_384)
        {
            return null;
        }

        var endpoint =
            $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={Uri.EscapeDataString(options.FirebaseWebApiKey)}";
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                endpoint,
                new LookupRequest(idToken),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<LookupResponse>(cancellationToken);
            var user = payload?.Users?.FirstOrDefault();
            if (user is null || string.IsNullOrWhiteSpace(user.LocalId))
            {
                return null;
            }

            var provider = user.ProviderUserInfo?
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ProviderId))?
                .ProviderId
                ?? "firebase";
            return new FirebaseIdentity(
                user.LocalId.Trim(),
                user.Email?.Trim() ?? string.Empty,
                user.DisplayName?.Trim() ?? string.Empty,
                user.EmailVerified,
                provider.Trim());
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LookupRequest(
        [property: JsonPropertyName("idToken")] string IdToken);

    private sealed class LookupResponse
    {
        [JsonPropertyName("users")]
        public List<FirebaseUser>? Users { get; init; }
    }

    private sealed class FirebaseUser
    {
        [JsonPropertyName("localId")]
        public string? LocalId { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("emailVerified")]
        public bool EmailVerified { get; init; }

        [JsonPropertyName("providerUserInfo")]
        public List<ProviderInfo>? ProviderUserInfo { get; init; }
    }

    private sealed class ProviderInfo
    {
        [JsonPropertyName("providerId")]
        public string? ProviderId { get; init; }
    }
}
