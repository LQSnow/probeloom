namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static void ManagesRequestHistory()
    {
        var history = new RequestHistory(2);
        var firstNode = Guid.NewGuid();
        var secondNode = Guid.NewGuid();
        var first = CreateHistoryResult(firstNode, "first");
        var second = CreateHistoryResult(secondNode, "second");
        var latest = CreateHistoryResult(firstNode, "latest");
        history.Add(first);
        history.Add(second);
        history.Add(latest);

        Equal(2, history.Entries.Count);
        Equal(latest.Id, history.LatestFor(firstNode)!.Id);
        True(history.Entries.All(entry => entry.Id != first.Id), "Oldest history entry was not evicted.");
        history.Clear();
        Equal(0, history.Entries.Count);
    }

}
