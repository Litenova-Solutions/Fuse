using System.Diagnostics.CodeAnalysis;
using Fuse.Paths;
using Microsoft.CodeAnalysis;

namespace Fuse.Workspace;

/// <summary>
///     The views with some files at HEAD content that are kept for sessions, least recently used first. Past
///     <see cref="MaxKept"/> the view used least recently goes, and a later session derives it again from the
///     current view, which answers the same. It is locked because checks that run at the same time derive and
///     shed views.
/// </summary>
internal sealed class SessionViews
{
    /// <summary>The kept views, least recently used first.</summary>
    private readonly List<Entry> _entries = [];

    /// <summary>Guards <see cref="_entries"/>, which checks that run at the same time derive and shed.</summary>
    private readonly Lock _lock = new();

    /// <summary>The most session views kept. Past it the view used least recently goes, and a later session derives it again from the current view, which answers the same.</summary>
    internal const int MaxKept = 8;

    /// <summary>How many views are kept now.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>One kept session view: the view it was derived from, the files it holds at HEAD, and the view itself.</summary>
    /// <param name="Source">The current view the kept view is in step with. A view of another source is derived again.</param>
    /// <param name="Paths">The files the view holds at HEAD, as one key in the order their relative paths sort.</param>
    /// <param name="HeadPaths">The files the view holds at HEAD.</param>
    /// <param name="View">The kept view.</param>
    internal sealed record Entry(Solution Source, string Paths, IReadOnlyList<RepoPath> HeadPaths, Solution View);

    /// <summary>Returns the kept view for <paramref name="source"/> and <paramref name="paths"/> when one is kept, and moves it to most recently used.</summary>
    /// <param name="source">The current view the kept view is in step with.</param>
    /// <param name="paths">The files the view holds at HEAD, as one key in the order their relative paths sort.</param>
    /// <param name="view">The kept view, when one is kept for that source and key.</param>
    /// <returns>True when a view is kept for that source and key.</returns>
    public bool TryTake(Solution source, string paths, [NotNullWhen(true)] out Solution? view)
    {
        lock (_lock)
        {
            var kept = _entries.FindIndex(v => ReferenceEquals(v.Source, source) && v.Paths == paths);
            if (kept >= 0)
            {
                var entry = _entries[kept];
                _entries.RemoveAt(kept);
                _entries.Add(entry);
                view = entry.View;
                return true;
            }
        }

        view = null;
        return false;
    }

    /// <summary>Keeps <paramref name="view"/> for <paramref name="source"/> and <paramref name="paths"/>, unless one is already kept for them, and drops the view used least recently past <see cref="MaxKept"/>.</summary>
    /// <param name="source">The current view the kept view is in step with.</param>
    /// <param name="paths">The files the view holds at HEAD, as one key in the order their relative paths sort.</param>
    /// <param name="headPaths">The files the view holds at HEAD.</param>
    /// <param name="view">The view to keep.</param>
    public void Add(Solution source, string paths, IReadOnlyList<RepoPath> headPaths, Solution view)
    {
        lock (_lock)
        {
            if (!_entries.Any(v => ReferenceEquals(v.Source, source) && v.Paths == paths))
            {
                _entries.Add(new Entry(source, paths, headPaths, view));
                while (_entries.Count > MaxKept)
                    _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>
    ///     The kept session views derived from <paramref name="source"/>, as a copy the writer then updates one by one.
    ///     With no view kept, which is every single-agent sync, it returns at once, so the writer does no session view
    ///     work at all.
    /// </summary>
    /// <param name="source">The current view the kept views are in step with.</param>
    /// <returns>The kept views derived from that source.</returns>
    public List<Entry> From(Solution source)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
                return [];
            return [.. _entries.Where(v => ReferenceEquals(v.Source, source))];
        }
    }

    /// <summary>A copy of every kept view, or an empty list when none is kept.</summary>
    /// <returns>The kept views in the order of use, least recently used first.</returns>
    public List<Entry> All()
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
                return [];
            return [.. _entries];
        }
    }

    /// <summary>Replaces <paramref name="entry"/> with its view updated to <paramref name="view"/> of <paramref name="source"/>, keeping its place in the order of use.</summary>
    /// <param name="entry">The kept view to replace.</param>
    /// <param name="source">The current view the kept view is in step with now.</param>
    /// <param name="view">The updated view.</param>
    public void Replace(Entry entry, Solution source, Solution view)
    {
        lock (_lock)
        {
            var index = _entries.IndexOf(entry);
            if (index >= 0)
                _entries[index] = entry with { Source = source, View = view };
        }
    }

    /// <summary>Sheds the kept session views. A later check derives its view from the current view again and is answered the same, only slower.</summary>
    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }
}
