using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProbeLoom.Core;

namespace ProbeLoom.Presentation;

public sealed partial class RoutePartsEditor : UserControl
{
    private readonly ObservableCollection<RoutePart> _items = [];

    public RoutePartsEditor()
    {
        InitializeComponent();
        PartsList.ItemsSource = _items;
    }

    public void SetItems(IEnumerable<RoutePart> parts)
    {
        _items.Clear();
        foreach (var part in parts)
        {
            _items.Add(part.Clone());
        }
    }

    public IReadOnlyList<RoutePart> GetItems() => _items.Select(part => part.Clone()).ToArray();

    private void Add_Click(object sender, RoutedEventArgs e) =>
        _items.Add(new RoutePart { Name = "Route Part", Value = "/", IsEnabled = true });

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RoutePart part })
        {
            var index = _items.IndexOf(part);
            if (index > 0)
            {
                _items.Move(index, index - 1);
            }
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RoutePart part })
        {
            var index = _items.IndexOf(part);
            if (index >= 0 && index < _items.Count - 1)
            {
                _items.Move(index, index + 1);
            }
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RoutePart part })
        {
            _items.Remove(part);
        }
    }
}
