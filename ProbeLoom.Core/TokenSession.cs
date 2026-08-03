using System.Text.Json;

namespace ProbeLoom.Core;

public sealed record TokenSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset UpdatedAt)
{
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);

    public bool IsExpired(DateTimeOffset now, TimeSpan? clockSkew = null) =>
        ExpiresAt is DateTimeOffset expiry &&
        expiry <= now.Add(clockSkew ?? TimeSpan.FromSeconds(15));
}

public sealed class TokenSessionStore(ISecureValueStore secureValueStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TokenSession?> LoadAsync(
        Guid projectId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        var json = await secureValueStore.GetAsync(
            SecureValueKeys.TokenSession(projectId, environmentId),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TokenSession>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(
        Guid projectId,
        Guid environmentId,
        TokenSession session,
        CancellationToken cancellationToken = default) =>
        secureValueStore.SetAsync(
            SecureValueKeys.TokenSession(projectId, environmentId),
            JsonSerializer.Serialize(session, JsonOptions),
            cancellationToken);

    public Task ClearAsync(
        Guid projectId,
        Guid environmentId,
        CancellationToken cancellationToken = default) =>
        secureValueStore.RemoveAsync(
            SecureValueKeys.TokenSession(projectId, environmentId),
            cancellationToken);
}
