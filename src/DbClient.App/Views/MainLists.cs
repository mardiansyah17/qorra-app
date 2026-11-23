using System.ComponentModel;
using System.Windows.Data;
using DbClient.Core.Models;
using DbClient.Core.Services;
using DbClient.ViewModels;

namespace DbClient.App.Views;

public sealed class MainLists
{
    private readonly MainViewModel _vm;

    public MainLists(MainViewModel vm)
    {
        _vm = vm;
        FilteredConnections = new ListCollectionView(vm.Connections);
        GroupedConnections = new ListCollectionView(vm.Connections)
        {
            CustomSort = Comparer<object>.Create((a, b) => vm.CompareByGroup((ConnectionProfile)a, (ConnectionProfile)b)),
        };
        GroupedConnections.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionProfile.Color),
            new FuncConverter(c => ConnectionTags.GroupFor(c as string))));
        FilteredTables = new ListCollectionView(vm.Tables);
        FilteredViews = new ListCollectionView(vm.Views);
        FilteredHistory = new ListCollectionView(vm.History) { Filter = o => vm.InHistoryFilter((HistoryEntry)o) };
        FilteredConnections.Filter = Wrap(vm.HomeFilter);
        GroupedConnections.Filter = Wrap(vm.SidebarFilter);
        FilteredTables.Filter = FilteredViews.Filter = Wrap(vm.ObjectFilter);
        ApplySort();
        vm.PropertyChanged += OnChanged;
        vm.LastUsedChanged += OnLastUsedChanged;
    }

    public ListCollectionView FilteredConnections { get; }
    public ListCollectionView GroupedConnections { get; }
    public ListCollectionView FilteredTables { get; }
    public ListCollectionView FilteredViews { get; }
    public ListCollectionView FilteredHistory { get; }

    public void Detach()
    {
        _vm.PropertyChanged -= OnChanged;
        _vm.LastUsedChanged -= OnLastUsedChanged;
        FilteredConnections.DetachFromSourceCollection();
        GroupedConnections.DetachFromSourceCollection();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HomeFilter):
                FilteredConnections.Filter = Wrap(_vm.HomeFilter);
                break;
            case nameof(MainViewModel.SidebarFilter):
                GroupedConnections.Filter = Wrap(_vm.SidebarFilter);
                break;
            case nameof(MainViewModel.ObjectFilter):
                FilteredTables.Filter = FilteredViews.Filter = Wrap(_vm.ObjectFilter);
                break;
            case nameof(MainViewModel.SortByName):
                ApplySort();
                break;
            case nameof(MainViewModel.HistoryFilter):
                FilteredHistory.Refresh();
                break;
        }
    }

    private void OnLastUsedChanged(object? sender, EventArgs e) => FilteredConnections.Refresh();

    private static Predicate<object>? Wrap<T>(Predicate<T>? filter) => filter is null ? null : item => filter((T)item);

    private void ApplySort()
    {
        using (FilteredConnections.DeferRefresh())
        {
            FilteredConnections.SortDescriptions.Clear();
            FilteredConnections.SortDescriptions.Add(_vm.SortByName
                ? new SortDescription(nameof(ConnectionProfile.Name), ListSortDirection.Ascending)
                : new SortDescription(nameof(ConnectionProfile.LastUsed), ListSortDirection.Descending));
        }
    }
}
