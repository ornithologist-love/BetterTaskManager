namespace SystemInformer.Client.Processes;

/// <summary>Retains the forest by live row identity; a reused PID must not inherit the old branch.</summary>
internal sealed class ProcessHierarchy
{
    private sealed class Node(ProcessRow row)
    {
        public readonly ProcessRow Row = row;
        public uint ParentId;
        public bool HasWindow, Seen, Completed, InChain, SectionKnown, Foreground;
        public Node? Parent, ActualParent;
        public readonly List<Node> Children = [];
    }

    private readonly Dictionary<uint, Node> _byId = new();
    private readonly List<Node> _nodes = [], _roots = [], _chain = [], _ordered = [];
    private readonly List<uint> _removed = [];
    private readonly HashSet<Node> _included = [], _descended = [];
    private readonly Stack<Node> _matchingBranches = new();
    private readonly Stack<(Node Node, int Depth, bool Visible)> _pending = new();
    private bool _sections;

    public void Update(IReadOnlyList<ProcessRow> rows, Predicate<ProcessRow> matches, bool filtering,
        Comparison<ProcessRow> comparison, bool tree = true, bool sections = false)
    {
        var topologyChanged = sections != _sections;
        foreach (var node in _nodes) node.Seen = false;
        _nodes.Clear();
        foreach (var row in rows)
        {
            if (!_byId.TryGetValue(row.Id, out var node) || !ReferenceEquals(node.Row, row))
            {
                _byId[row.Id] = node = new(row);
                topologyChanged = true;
            }
            var hasWindow = row.WindowRole is "前台" or "有窗口";
            if (node.ParentId != row.ParentId || node.HasWindow != hasWindow) topologyChanged = true;
            node.ParentId = row.ParentId;
            node.HasWindow = hasWindow;
            node.Seen = true;
            _nodes.Add(node);
        }
        foreach (var (id, node) in _byId)
            if (!node.Seen) _removed.Add(id);
        if (_removed.Count != 0) topologyChanged = true;
        foreach (var id in _removed) _byId.Remove(id);
        _removed.Clear();
        if (topologyChanged) Rebuild(sections);

        int Compare(Node left, Node right) => sections && left.Row.Section != right.Row.Section
            ? (left.Row.Section == ProcessRow.ForegroundSection ? -1 : 1) : comparison(left.Row, right.Row);
        if (!tree)
        {
            _ordered.AddRange(_nodes);
            _ordered.Sort(Compare);
            for (var i = 0; i < _ordered.Count; i++)
            {
                var node = _ordered[i];
                node.Row.SetTree(0, i, !filtering || matches(node.Row), false, filtering, node.ActualParent?.Row.DisplayName ?? "");
            }
            _ordered.Clear();
            return;
        }

        // Structure survives a sample; resource/name sorting and search still use the latest values.
        _roots.Sort(Compare);
        foreach (var node in _nodes) node.Children.Sort(Compare);
        if (filtering)
        {
            foreach (var node in _nodes)
            {
                if (!matches(node.Row)) continue;
                for (var current = node; current != null && _included.Add(current); current = current.Parent) { }
                _matchingBranches.Push(node);
            }
            while (_matchingBranches.TryPop(out var match))
            {
                if (!_descended.Add(match)) continue;
                _included.Add(match);
                foreach (var child in match.Children) _matchingBranches.Push(child);
            }
        }
        for (var i = _roots.Count - 1; i >= 0; i--) _pending.Push((_roots[i], 0, true));
        var order = 0;
        while (_pending.TryPop(out var item))
        {
            var node = item.Node;
            var visible = item.Visible && (!filtering || _included.Contains(node));
            node.Row.SetTree(item.Depth, order++, visible, node.Children.Count != 0, filtering, node.ActualParent?.Row.DisplayName ?? "");
            var showChildren = visible && (filtering || node.Row.IsExpanded);
            for (var i = node.Children.Count - 1; i >= 0; i--) _pending.Push((node.Children[i], item.Depth + 1, showChildren));
        }
        _included.Clear();
        _descended.Clear();
    }

    private void Rebuild(bool sections)
    {
        _sections = sections;
        _roots.Clear();
        foreach (var node in _nodes)
        {
            node.Children.Clear();
            node.Completed = node.InChain = node.SectionKnown = node.Foreground = false;
            node.Parent = node.ParentId != 0 && node.ParentId != node.Row.Id && _byId.TryGetValue(node.ParentId, out var parent)
                && node.Row.Created is ulong created && parent.Row.Created is ulong parentCreated && parentCreated <= created ? parent : null;
        }
        // Equal timestamps or malformed parent data must never create a traversal cycle.
        foreach (var node in _nodes)
        {
            for (var current = node; current != null && !current.Completed; current = current.Parent)
            {
                if (current.InChain) { current.Parent = null; break; }
                current.InChain = true;
                _chain.Add(current);
            }
            foreach (var current in _chain) { current.Completed = true; current.InChain = false; }
            _chain.Clear();
        }
        foreach (var node in _nodes)
        {
            node.ActualParent = node.Parent;
            if (sections && node.HasWindow) node.Parent = null;
        }
        foreach (var node in _nodes)
        {
            var foreground = false;
            for (var current = node; sections && current != null; current = current.Parent)
            {
                if (current.SectionKnown) { foreground = current.Foreground; break; }
                _chain.Add(current);
                if (current.HasWindow) { foreground = true; break; }
            }
            foreach (var current in _chain) { current.SectionKnown = true; current.Foreground = foreground; }
            _chain.Clear();
            node.Row.SetSection(foreground);
        }
        foreach (var node in _nodes)
        {
            if (sections && node.Parent is { } parent && node.Row.Section != parent.Row.Section) node.Parent = null;
            if (node.Parent != null) node.Parent.Children.Add(node);
            else _roots.Add(node);
        }
    }
}
