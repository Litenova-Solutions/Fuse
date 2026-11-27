using System.Collections.Concurrent;
using System.Security.Cryptography;
using Fuse.Paths;
using Fuse.Workspace.Views;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Process-wide immutable resource cache shared across the primary workspace and every worktree overlay so
///     metadata tables, analyzer assemblies and unchanged HEAD texts are materialized once.
/// </summary>
internal sealed class BlueprintResourceCache
{
    // At most this many HEAD texts stay cached; past it the least recently used goes.
    private const int MaxHeadTexts = 1024;

    private readonly IAnalyzerAssemblyLoader _analyzerLoader;
    private readonly ConcurrentDictionary<string, (long Length, long Ticks, MetadataReference Reference)> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Length, long Ticks, AnalyzerFileReference Reference)> _analyzers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _headLock = new();
    private readonly Dictionary<(string Head, string Relative), LinkedListNode<HeadTextEntry>> _headTexts = [];
    private readonly LinkedList<HeadTextEntry> _headOrder = [];

    public BlueprintResourceCache(IAnalyzerAssemblyLoader analyzerLoader) => _analyzerLoader = analyzerLoader;

    public int MetadataCount => _metadata.Count;

    public int HeadTextCount
    {
        get
        {
            lock (_headLock)
                return _headTexts.Count;
        }
    }

    public MetadataReference? GetMetadataReference(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
            if (_metadata.TryGetValue(path, out var kept) && kept.Length == stamp.Length && kept.Ticks == stamp.Ticks)
                return kept.Reference;
            var reference = MetadataReference.CreateFromFile(path);
            _metadata[path] = (stamp.Length, stamp.Ticks, reference);
            return reference;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public AnalyzerFileReference? GetAnalyzerReference(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
            if (_analyzers.TryGetValue(path, out var kept) && kept.Length == stamp.Length && kept.Ticks == stamp.Ticks)
                return kept.Reference;
            _analyzerLoader.AddDependencyLocation(path);
            var reference = new AnalyzerFileReference(path, _analyzerLoader);
            _analyzers[path] = (stamp.Length, stamp.Ticks, reference);
            return reference;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public TextAndVersion? GetOrReadHeadText(string head, string relativePath, string diskPath)
    {
        byte[] bytes;
        try
        {
            bytes = SharedRead.AllBytes(diskPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var key = (head, relativePath);
        lock (_headLock)
        {
            if (_headTexts.TryGetValue(key, out var node) && string.Equals(node.Value.ContentHash, hash, StringComparison.Ordinal))
            {
                _headOrder.Remove(node);
                _headOrder.AddFirst(node);
                return TextAndVersion.Create(node.Value.Text, VersionStamp.Create(), diskPath);
            }

            var text = SolutionViews.Decode(bytes);
            if (node is not null)
            {
                node.Value = new HeadTextEntry(key, hash, text);
                _headOrder.Remove(node);
                _headOrder.AddFirst(node);
            }
            else
            {
                _headTexts[key] = _headOrder.AddFirst(new HeadTextEntry(key, hash, text));
                while (_headTexts.Count > MaxHeadTexts && _headOrder.Last is { } last)
                {
                    _headOrder.RemoveLast();
                    _headTexts.Remove(last.Value.Key);
                }
            }

            return TextAndVersion.Create(text, VersionStamp.Create(), diskPath);
        }
    }

    public void TrimUnused(Solution? activeSolution)
    {
        lock (_headLock)
        {
            _headTexts.Clear();
            _headOrder.Clear();
        }
        if (activeSolution is null)
        {
            _metadata.Clear();
            return;
        }

        var inUse = new HashSet<MetadataReference>();
        foreach (var project in activeSolution.Projects)
        {
            foreach (var reference in project.MetadataReferences)
                inUse.Add(reference);
        }

        foreach (var key in _metadata.Keys.ToList())
        {
            if (_metadata.TryGetValue(key, out var entry) && !inUse.Contains(entry.Reference))
                _metadata.TryRemove(key, out _);
        }
    }

    private sealed record HeadTextEntry((string Head, string Relative) Key, string ContentHash, SourceText Text);
}
