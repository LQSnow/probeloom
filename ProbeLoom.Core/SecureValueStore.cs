namespace ProbeLoom.Core;

public class SecureValueStoreException : Exception
{
    public SecureValueStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface ISecureValueStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public static class SecureValueKeys
{
    public static string Variable(Guid projectId, Guid variableId) =>
        $"project:{projectId:N}:variable:{variableId:N}";

    public static string TokenSession(Guid projectId, Guid environmentId) =>
        $"project:{projectId:N}:environment:{environmentId:N}:tokens";
}

public sealed class InMemorySecureValueStore : ISecureValueStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.Remove(key);
        return Task.CompletedTask;
    }
}
