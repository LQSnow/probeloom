namespace ProbeLoom.Core;

public sealed class RequestHistory
{
    private readonly List<HttpExecutionResult> _entries = [];

    public RequestHistory(int capacity = 30)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public IReadOnlyList<HttpExecutionResult> Entries => _entries;

    public void Add(HttpExecutionResult result)
    {
        _entries.Insert(0, result);
        if (_entries.Count > Capacity)
        {
            _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
    }

    public HttpExecutionResult? LatestFor(Guid requestNodeId) =>
        _entries.FirstOrDefault(entry => entry.RequestNodeId == requestNodeId);

    public void Clear() => _entries.Clear();
}
