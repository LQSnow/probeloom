using System.Collections.ObjectModel;

namespace ProbeLoom.Core;

public sealed class TransactionalSecureValueStore : ISecureValueStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> _loadAsync;
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task> _saveAsync;
    private Dictionary<string, string>? _values;

    public TransactionalSecureValueStore(
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> loadAsync,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(loadAsync);
        ArgumentNullException.ThrowIfNull(saveAsync);
        _loadAsync = loadAsync;
        _saveAsync = saveAsync;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await EnsureLoadedAsync(cancellationToken);
            return values.GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await EnsureLoadedAsync(cancellationToken);
            var nextValues = new Dictionary<string, string>(values, StringComparer.Ordinal)
            {
                [key] = value
            };

            await PersistAsync(nextValues, cancellationToken);
            _values = nextValues;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await EnsureLoadedAsync(cancellationToken);
            if (!values.ContainsKey(key))
            {
                return;
            }

            var nextValues = new Dictionary<string, string>(values, StringComparer.Ordinal);
            nextValues.Remove(key);

            await PersistAsync(nextValues, cancellationToken);
            _values = nextValues;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_values is not null)
        {
            return _values;
        }

        var loadedValues = await _loadAsync(cancellationToken);
        _values = new Dictionary<string, string>(loadedValues, StringComparer.Ordinal);
        return _values;
    }

    private Task PersistAsync(
        Dictionary<string, string> values,
        CancellationToken cancellationToken) =>
        _saveAsync(new ReadOnlyDictionary<string, string>(values), cancellationToken);
}
