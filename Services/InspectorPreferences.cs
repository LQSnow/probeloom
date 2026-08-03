using ProbeLoom.Core;
using Windows.Storage;

namespace ProbeLoom.Services;

public sealed class InspectorPreferences
{
    private const string ExpandedKey = "Inspector.IsExpanded";
    private const string WidthKey = "Inspector.Width";
    private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

    public InspectorLayoutState Load()
    {
        var isExpanded = _settings.Values[ExpandedKey] as bool? ?? true;
        var width = _settings.Values[WidthKey] switch
        {
            double value => value,
            int value => value,
            _ => InspectorLayoutState.DefaultWidth
        };
        return new InspectorLayoutState(isExpanded, width).Normalize();
    }

    public void Save(InspectorLayoutState state)
    {
        var normalized = state.Normalize();
        _settings.Values[ExpandedKey] = normalized.IsExpanded;
        _settings.Values[WidthKey] = normalized.Width;
    }
}
