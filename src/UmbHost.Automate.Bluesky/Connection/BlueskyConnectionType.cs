using Umbraco.Automate.Core.Connections;
using UmbHost.Automate.Bluesky.Http;

namespace UmbHost.Automate.Bluesky.Connection;

/// <summary>
/// Bluesky connection type. Validates by signing in with the app password.
/// </summary>
[ConnectionType("bluesky", "Bluesky",
    Description = "Connect to a Bluesky account using an app password",
    Group = "Social",
    Icon = "icon-cloud")]
public sealed class BlueskyConnectionType : ConnectionTypeBase<BlueskyConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public BlueskyConnectionType(ConnectionTypeInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not BlueskyConnectionSettings typed
            || string.IsNullOrWhiteSpace(typed.Identifier)
            || string.IsNullOrWhiteSpace(typed.AppPassword))
        {
            return ConnectionValidationResult.Failure("A handle and app password are required.");
        }

        if (!Uri.TryCreate(typed.ServiceUrl, UriKind.Absolute, out var service) || service.Scheme != Uri.UriSchemeHttps)
        {
            return ConnectionValidationResult.Failure("Service URL must be an https URL.");
        }

        using var client = BlueskyClient.Create(_httpClientFactory, typed);
        try
        {
            var (session, status, _) = await BlueskyClient.CreateSessionAsync(client, typed, cancellationToken);
            return session is not null
                ? ConnectionValidationResult.Success($"Signed in as @{session.Handle}.")
                : ConnectionValidationResult.Failure($"Bluesky rejected the sign-in ({(int)status}).");
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach Bluesky.", [ex.Message]);
        }
    }
}
