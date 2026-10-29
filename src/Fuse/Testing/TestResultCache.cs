using System.Globalization;
using System.Text.Json.Nodes;
using Fuse.Testing.Model;

namespace Fuse.Testing;

/// <summary>
///     The passes of recent shadow runs by test class and input key, so a plan whose inputs did not change answers them
///     without running them. It lives in memory and in <c>test-result-cache.json</c> in the state directory, replaced
///     atomically, so an engine that restarts keeps this build's passes and ignores any other build's file.
/// </summary>
internal sealed class TestResultCache
{
    /// <summary>The most classes kept; past it the class unused the longest goes.</summary>
    internal const int MaxEntries = 2048;

    private const string FileName = "test-result-cache.json";
    private const int Version = 1;

    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly string _build;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<(string Project, string Class), CachedPass> _entries = new();
    private readonly List<(string Project, string Class)> _order = [];
    private bool _loaded;

    /// <param name="stateDirectory">The state directory the store is read from and written to.</param>
    /// <param name="log">Receives why a stored file was ignored.</param>
    /// <param name="build">This build's id; a file of another build is ignored.</param>
    public TestResultCache(string stateDirectory, Action<string> log, string build)
    {
        _directory = stateDirectory;
        _log = log;
        _build = build;
    }

    /// <summary>How many classes are kept, for a test.</summary>
    internal int Count
    {
        get
        {
            lock (_cacheLock)
            {
                LoadLocked();
                return _entries.Count;
            }
        }
    }

    /// <summary>The kept pass for the class, or false when none is kept.</summary>
    public bool TryGet(string project, string className, out CachedPass pass)
    {
        lock (_cacheLock)
        {
            LoadLocked();
            var key = (project, className);
            if (!_entries.TryGetValue(key, out var found))
            {
                pass = null!;
                return false;
            }

            _order.Remove(key);
            _order.Add(key);
            pass = found;
            return true;
        }
    }

    /// <summary>Keeps <paramref name="pass"/>, forgetting the class unused the longest past the cap.</summary>
    public void Store(CachedPass pass)
    {
        lock (_cacheLock)
        {
            LoadLocked();
            var key = (pass.Project, pass.Class);
            _entries[key] = pass;
            _order.Remove(key);
            _order.Add(key);
            while (_order.Count > MaxEntries)
            {
                _entries.Remove(_order[0]);
                _order.RemoveAt(0);
            }

            PersistLocked();
        }
    }

    /// <summary>Forgets the kept pass for the class, if one is kept.</summary>
    public void Drop(string project, string className)
    {
        lock (_cacheLock)
        {
            LoadLocked();
            if (_entries.Remove((project, className)))
            {
                _order.Remove((project, className));
                PersistLocked();
            }
        }
    }

    /// <summary>Reads the stored file once, ignoring it after logging when it is corrupt or of another build.</summary>
    private void LoadLocked()
    {
        if (_loaded)
            return;
        _loaded = true;
        var path = Path.Combine(_directory, FileName);
        if (!File.Exists(path))
            return;
        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed is null || (int?)parsed["version"] != Version)
            {
                _log("test result cache: ignoring a file of another version");
                return;
            }

            if (parsed["build"]?.GetValue<string>() != _build)
            {
                _log("test result cache: ignoring a file of another build");
                return;
            }

            if (parsed["entries"] is not JsonArray entries)
            {
                _log("test result cache: ignoring a file without entries");
                return;
            }

            // Stored least recently used first, so past the cap the tail is what fits in memory.
            foreach (var node in entries.TakeLast(MaxEntries))
            {
                if (node is not JsonObject entry)
                    continue;
                CachedPass? pass;
                try
                {
                    pass = ReadEntry(entry);
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
                {
                    continue;
                }

                if (pass is null)
                    continue;
                var key = (pass.Project, pass.Class);
                _entries[key] = pass;
                _order.Remove(key);
                _order.Add(key);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            _log($"test result cache: ignoring a file that cannot be read ({e.Message})");
            _entries.Clear();
            _order.Clear();
        }
    }

    /// <summary>One stored entry as a kept pass, or null when it leaves out what a pass holds.</summary>
    private static CachedPass? ReadEntry(JsonObject entry)
    {
        if (entry["project"]?.GetValue<string>() is not { Length: > 0 } project
            || entry["class"]?.GetValue<string>() is not { Length: > 0 } className
            || entry["key"]?.GetValue<string>() is not { Length: > 0 } key
            || entry["tests"] is not JsonArray tests
            || entry["storedUtc"]?.GetValue<string>() is not string stored
            || !DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var storedUtc))
            return null;
        var names = new List<string>(tests.Count);
        foreach (var node in tests)
        {
            if (node?.GetValue<string>() is not { Length: > 0 } name)
                return null;
            names.Add(name);
        }

        return new CachedPass(project, className, key, [.. names], storedUtc.ToUniversalTime());
    }

    /// <summary>Replaces the stored file through a temporary file beside it. A file that cannot be written is logged and skipped.</summary>
    private void PersistLocked()
    {
        try
        {
            var entries = new JsonArray();
            foreach (var key in _order)
            {
                var pass = _entries[key];
                var tests = new JsonArray();
                foreach (var test in pass.Tests)
                    tests.Add((JsonNode?)JsonValue.Create(test));
                entries.Add((JsonNode)new JsonObject
                {
                    ["project"] = pass.Project,
                    ["class"] = pass.Class,
                    ["key"] = pass.Key,
                    ["tests"] = tests,
                    ["storedUtc"] = pass.StoredUtc.ToString("o", CultureInfo.InvariantCulture),
                });
            }

            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileName);
            var temp = path + ".fuse-tmp";
            File.WriteAllText(temp, new JsonObject { ["version"] = Version, ["build"] = _build, ["entries"] = entries }.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log($"test result cache: could not write the file ({e.Message})");
            try
            {
                File.Delete(Path.Combine(_directory, FileName + ".fuse-tmp"));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
