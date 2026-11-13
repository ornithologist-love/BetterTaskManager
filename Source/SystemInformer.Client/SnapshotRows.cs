using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SystemInformer.Client;

internal sealed record RowBatch(bool MembershipChanged, int Added, int Removed, int Updated,
    IReadOnlySet<string> ChangedProperties);

/// <summary>Single-owner snapshot reconciliation. Retains rows and publishes one completed structural change.</summary>
internal sealed class SnapshotRows<TSample, TKey, TRow>(
    Func<TSample, TKey> identity, Func<TSample, TRow> create, Action<TRow, TSample> update,
    IEqualityComparer<TKey>? comparer = null) : ObservableCollection<TRow>
    where TKey : notnull
    where TRow : ObservableRow
{
    private readonly Dictionary<TKey, TRow> _index = new(comparer);

    public RowBatch Apply(IReadOnlyList<TSample> samples, Action<IReadOnlyList<TRow>>? arrange = null)
    {
        CheckReentrancy();
        var alive = new HashSet<TKey>(_index.Comparer);
        var added = 0;
        foreach (var sample in samples)
        {
            var key = identity(sample);
            alive.Add(key);
            if (_index.TryGetValue(key, out var row))
            {
                update(row, sample);
            }
            else
            {
                row = create(sample);
                _index.Add(key, row);
                // Bypass per-item notifications: observers must see the completed snapshot.
                Items.Add(row);
                added++;
            }
        }

        var removedRows = new HashSet<TRow>();
        foreach (var key in _index.Keys.Where(key => !alive.Contains(key)).ToArray())
        {
            removedRows.Add(_index[key]);
            _index.Remove(key);
        }
        if (removedRows.Count != 0)
        {
            // Compact once, then remove the tail; repeated middle removals would shift rows repeatedly.
            var retained = 0;
            for (var i = 0; i < Items.Count; i++)
                if (!removedRows.Contains(Items[i])) Items[retained++] = Items[i];
            while (Items.Count > retained) Items.RemoveAt(Items.Count - 1);
        }

        var membershipChanged = added != 0 || removedRows.Count != 0;
        arrange?.Invoke(this);
        return Publish(membershipChanged, added, removedRows.Count);
    }

    public RowBatch Arrange(Action<IReadOnlyList<TRow>> arrange)
    {
        CheckReentrancy();
        arrange(this);
        return Publish(false, 0, 0);
    }

    private RowBatch Publish(bool membershipChanged, int added, int removed)
    {
        var changedRows = Items.Where(row => row.PendingChanges.Count != 0).ToArray();
        var changedProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in changedRows) changedProperties.UnionWith(row.PendingChanges);
        if (membershipChanged)
        {
            if (added != removed) OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        // Every row's values and membership are now final, before any binding reads them.
        foreach (var row in changedRows) row.PublishChanges();
        return new(membershipChanged, added, removed, changedRows.Length, changedProperties);
    }
}
