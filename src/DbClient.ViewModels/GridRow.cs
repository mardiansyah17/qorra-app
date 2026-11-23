using System.ComponentModel;

namespace DbClient.ViewModels;

public enum RowState { Unchanged, Modified, Added, Deleted }

public sealed class GridRow : INotifyPropertyChanged
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool[]> Unedited = new();
    private RowState _state;
    private bool[] _changed;

    public GridRow(object?[] values, RowState state = RowState.Unchanged)
    {
        Values = values;
        _changed = Unedited.GetOrAdd(values.Length, n => new bool[n]);
        _state = state;
    }

    public int Number { get; init; }
    public object?[] Values { get; }
    public object?[]? Original { get; private set; }
    public IReadOnlyList<bool> Changed => _changed;

    public RowState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State));
        }
    }

    public object? this[int index]
    {
        get => Values[index];
        set => Set(index, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Set(int index, object? value)
    {
        var current = Values[index];
        if (Equals(current, value) || value is string s && current is not string
            && (s == DbClient.Core.DataCopy.Text(current) || s == CellEditors.BooleanText(current) || current is DateTime d && d == CellEditors.Parse(s) || current is TimeSpan t && t == CellEditors.ParseTime(s)))
            return;
        if (_state == RowState.Unchanged) Original = (object?[])Values.Clone();
        Values[index] = value;
        if (Unedited.TryGetValue(_changed.Length, out var shared) && shared == _changed) _changed = new bool[Values.Length];
        _changed[index] = true;
        if (_state == RowState.Unchanged) State = RowState.Modified;
        Notify("Item[]");
        Notify(nameof(Changed));
    }

    public void Revert()
    {
        if (Original is not null) Array.Copy(Original, Values, Values.Length);
        Original = null;
        _changed = Unedited.GetOrAdd(Values.Length, n => new bool[n]);
        State = RowState.Unchanged;
        Notify("Item[]");
        Notify(nameof(Changed));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
