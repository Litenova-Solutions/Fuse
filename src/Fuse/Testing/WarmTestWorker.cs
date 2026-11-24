using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Dotnet;

namespace Fuse.Testing;

/// <summary>
///     Out-of-process warm test worker (<c>fuse test-worker</c>). Keeps framework and NuGet assemblies JITted in the
///     default <see cref="AssemblyLoadContext"/>, loads mutable repository assemblies into a collectible
///     <see cref="AssemblyLoadContext"/> (Tier 1), supports in-place <see cref="MetadataUpdater.ApplyUpdate"/> IL delta
///     patching for body-only edits (Tier 2), and streams pass/fail verdicts and coverage hits back over a named pipe.
/// </summary>
internal sealed partial class WarmTestWorker
{
    private static readonly HashSet<string> SupportedTestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod", "SkippableFact", "SkippableTheory",
    };

    private static readonly HashSet<string> UnsupportedDataAttributes = new(StringComparer.Ordinal)
    {
        "MemberData", "ClassData", "TestCaseSource", "DynamicData", "TestFixtureSource", "ExpectedException",
    };

    private static readonly HashSet<string> UnsupportedLifecycleAttributes = new(StringComparer.Ordinal)
    {
        "OneTimeSetUp", "OneTimeTearDown", "ClassInitialize", "ClassCleanup", "AssemblyInitialize", "AssemblyCleanup", "SetUpFixture",
    };

    private static readonly HashSet<string> SetUpAttributes = new(StringComparer.Ordinal)
    {
        "SetUp", "TestInitialize",
    };

    private static readonly HashSet<string> TearDownAttributes = new(StringComparer.Ordinal)
    {
        "TearDown", "TestCleanup",
    };

    private static readonly OpCode[] OneByteOpCodes = BuildOneByteOpCodes();
    private static readonly OpCode[] TwoByteOpCodes = BuildTwoByteOpCodes();

    private readonly string _repoRoot;
    private readonly Lock _resolverGate = new();
    private string _binDir;
    private AssemblyDependencyResolver? _resolver;
    private string? _resolverAssemblyPath;
    private volatile HashSet<string> _mutableNames = new(StringComparer.OrdinalIgnoreCase);
    private ActiveTestContext? _activeContext;
    private bool _resolverAttached;

    private WarmTestWorker(string binDir, string? testAssemblyPath, string? repoRoot)
    {
        _binDir = binDir;
        _repoRoot = repoRoot ?? "";
        if (!string.IsNullOrEmpty(testAssemblyPath) && File.Exists(testAssemblyPath))
        {
            _resolverAssemblyPath = testAssemblyPath;
            _resolver = new AssemblyDependencyResolver(testAssemblyPath);
        }
    }

    /// <summary>Entry point invoked by <c>fuse test-worker</c>.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            await Console.Error.WriteLineAsync("fuse: test-worker requires the .NET runtime with dynamic code support").ConfigureAwait(false);
            return 2;
        }

        string? pipeName = null;
        string? binDir = null;
        string? testAssemblyPath = null;
        string? repoRoot = null;
        int? parentPid = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    pipeName = args[++i];
                    break;
                case "--bin" when i + 1 < args.Length:
                    binDir = args[++i];
                    break;
                case "--test-assembly" when i + 1 < args.Length:
                    testAssemblyPath = args[++i];
                    break;
                case "--repo-root" when i + 1 < args.Length:
                    repoRoot = args[++i];
                    break;
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
                    parentPid = parsed;
                    i++;
                    break;
                default:
                    await Console.Error.WriteLineAsync($"fuse: unknown test-worker option '{args[i]}'").ConfigureAwait(false);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            await Console.Error.WriteLineAsync("usage: fuse test-worker --pipe <name> [--bin <dir>] [--test-assembly <path>] [--repo-root <dir>] [--parent-pid <pid>]").ConfigureAwait(false);
            return 2;
        }

        return await RunDynamicGuardedAsync(pipeName, binDir ?? Environment.CurrentDirectory, testAssemblyPath, repoRoot, parentPid, cancellationToken).ConfigureAwait(false);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported in RunAsync; test-worker only runs on fuse.dll.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported in RunAsync; test-worker only runs on fuse.dll.")]
    private static Task<int> RunDynamicGuardedAsync(
        string pipeName,
        string binDir,
        string? testAssemblyPath,
        string? repoRoot,
        int? parentPid,
        CancellationToken cancellationToken)
    {
        var worker = new WarmTestWorker(binDir, testAssemblyPath, repoRoot);
        return worker.ServePipeAsync(pipeName, parentPid, cancellationToken);
    }

    [RequiresUnreferencedCode("WarmTestWorker dynamically loads and invokes test assemblies.")]
    [RequiresDynamicCode("WarmTestWorker creates collectible AssemblyLoadContexts.")]
    private async Task<int> ServePipeAsync(string pipeName, int? parentPid, CancellationToken cancellationToken)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? parentWatch = null;
        if (parentPid is > 0)
            parentWatch = WatchParentAsync(parentPid.Value, shutdown);

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000, shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
        {
            return 1;
        }

        using var writeLock = new SemaphoreSlim(1, 1);
        await SendEventAsync(pipe, writeLock, new WorkerEvent
        {
            Kind = "hello",
            ProcessId = Environment.ProcessId,
            SupportsMetadataUpdate = MetadataUpdater.IsSupported,
        }, shutdown.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(shutdown.Token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                WorkerCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize(line, WarmWorkerJson.Default.WorkerCommand);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (command is null)
                    continue;

                if (command.Kind == "shutdown")
                    break;

                if (command.Kind == "ping")
                {
                    await SendEventAsync(pipe, writeLock, new WorkerEvent { Kind = "pong", ProcessId = Environment.ProcessId }, shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                if (command.Kind == "unload")
                {
                    var unloaded = UnloadActiveContext();
                    await SendEventAsync(pipe, writeLock, new WorkerEvent { Kind = "unloaded", AlcUnloaded = unloaded }, shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                if (command.Kind == "run")
                    await HandleRunAsync(command, pipe, writeLock, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            UnloadActiveContext();
            await shutdown.CancelAsync().ConfigureAwait(false);
            if (parentWatch is not null)
            {
                try
                {
                    await parentWatch.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        return 0;
    }

    [RequiresUnreferencedCode("WarmTestWorker dynamically loads and invokes test assemblies.")]
    [RequiresDynamicCode("WarmTestWorker creates collectible AssemblyLoadContexts.")]
    private async Task HandleRunAsync(WorkerCommand command, Stream pipe, SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        if (!string.IsNullOrWhiteSpace(command.BinDirectory))
            _binDir = command.BinDirectory;
        EnsureDefaultResolver(command.TestAssemblyPath);

        var originalDir = Environment.CurrentDirectory;
        var workingDir = !string.IsNullOrWhiteSpace(command.WorkingDirectory) && Directory.Exists(command.WorkingDirectory)
            ? command.WorkingDirectory
            : Directory.Exists(_binDir) ? _binDir : originalDir;
        var rootForFrames = !string.IsNullOrWhiteSpace(command.RepoRoot) ? command.RepoRoot! : _repoRoot;

        try
        {
            Environment.CurrentDirectory = workingDir;
            var encApplied = false;
            ActiveTestContext? context = null;

            if (command.EncUpdates is { Length: > 0 } encUpdates)
            {
                if (TryApplyEncUpdates(encUpdates))
                {
                    context = _activeContext;
                    encApplied = true;
                }
                else
                {
                    // Fall back automatically to Tier 1 full ALC reload if full assemblies were supplied; otherwise ask pool for Tier 1.
                    UnloadActiveContext();
                    if (command.Assemblies is not { Length: > 0 })
                    {
                        await SendEventAsync(pipe, writeLock, new WorkerEvent
                        {
                            Kind = "fallback",
                            RunId = command.RunId,
                            Reason = "enc-reload-needed",
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            if (context is null)
            {
                UnloadActiveContext();
                if (command.Assemblies is not { Length: > 0 })
                {
                    await SendEventAsync(pipe, writeLock, new WorkerEvent
                    {
                        Kind = "fallback",
                        RunId = command.RunId,
                        Reason = "no-assemblies-supplied",
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                context = LoadTier1Context(command.Assemblies, command.TestAssemblySimpleName);
                _activeContext = context;
            }

            if (context.TestAssembly is null)
            {
                UnloadActiveContext();
                await SendEventAsync(pipe, writeLock, new WorkerEvent
                {
                    Kind = "fallback",
                    RunId = command.RunId,
                    Reason = $"could not load test assembly '{command.TestAssemblySimpleName}'",
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (CheckAssemblySafety(context.TestAssembly) is { } assemblyFallback)
            {
                UnloadActiveContext();
                await SendEventAsync(pipe, writeLock, new WorkerEvent
                {
                    Kind = "fallback",
                    RunId = command.RunId,
                    Reason = assemblyFallback,
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var cases = DiscoverTestCases(context.TestAssembly, command.SelectedPatterns, out var discoveryFallback);
            if (discoveryFallback is not null)
            {
                UnloadActiveContext();
                await SendEventAsync(pipe, writeLock, new WorkerEvent
                {
                    Kind = "fallback",
                    RunId = command.RunId,
                    Reason = discoveryFallback,
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var (passed, failed, skipped, stoppedEarly) = await ExecuteCasesAsync(
                context,
                cases,
                command,
                rootForFrames,
                pipe,
                writeLock,
                cancellationToken).ConfigureAwait(false);

            var alcUnloaded = false;
            if (!command.KeepAliveForEnc)
                alcUnloaded = UnloadActiveContext();

            started.Stop();
            await SendEventAsync(pipe, writeLock, new WorkerEvent
            {
                Kind = "complete",
                RunId = command.RunId,
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                EncApplied = encApplied,
                StoppedEarly = stoppedEarly,
                AlcUnloaded = alcUnloaded,
                TotalMs = started.Elapsed.TotalMilliseconds,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            UnloadActiveContext();
            await SendEventAsync(pipe, writeLock, new WorkerEvent
            {
                Kind = "fallback",
                RunId = command.RunId,
                Reason = $"warm worker execution error: {e.GetType().Name}: {e.Message}",
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Environment.CurrentDirectory = originalDir;
            }
            catch (IOException)
            {
            }
        }
    }

    [RequiresUnreferencedCode("Reads metadata and types from test assembly.")]
    private static bool TryApplyEncUpdates(WorkerEncDelta[] updates, ActiveTestContext? context)
    {
        if (context is null || !MetadataUpdater.IsSupported)
            return false;
        try
        {
            foreach (var delta in updates)
            {
                if (!context.LoadedAssemblies.TryGetValue(delta.SimpleName, out var assembly))
                    return false;
                var metadataDelta = Convert.FromBase64String(delta.MetadataBase64);
                var ilDelta = Convert.FromBase64String(delta.IlBase64);
                var pdbDelta = Convert.FromBase64String(delta.PdbBase64);
                MetadataUpdater.ApplyUpdate(assembly, metadataDelta, ilDelta, pdbDelta);
            }

            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    [RequiresUnreferencedCode("Reads metadata and types from test assembly.")]
    private bool TryApplyEncUpdates(WorkerEncDelta[] updates) =>
        TryApplyEncUpdates(updates, _activeContext);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [RequiresUnreferencedCode("Loads assemblies into collectible AssemblyLoadContext.")]
    [RequiresDynamicCode("Creates collectible AssemblyLoadContext.")]
    private ActiveTestContext LoadTier1Context(WorkerAssemblyPayload[] payloads, string testAssemblySimpleName)
    {
        var mutable = new Dictionary<string, (byte[] Pe, byte[] Pdb)>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in payloads)
        {
            var pe = Convert.FromBase64String(payload.PeBase64);
            var pdb = string.IsNullOrEmpty(payload.PdbBase64) ? [] : Convert.FromBase64String(payload.PdbBase64);
            mutable[payload.SimpleName] = (pe, pdb);
        }

        _mutableNames = new HashSet<string>(mutable.Keys, StringComparer.OrdinalIgnoreCase);
        var alc = new CollectibleTestLoadContext(_binDir, _resolver, mutable);
        Assembly? testAssembly = null;
        foreach (var simpleName in mutable.Keys)
        {
            var asm = alc.LoadFromAssemblyName(new AssemblyName(simpleName));
            if (simpleName.Equals(testAssemblySimpleName, StringComparison.OrdinalIgnoreCase))
                testAssembly = asm;
        }

        return new ActiveTestContext(alc, testAssembly, alc.LoadedAssemblies, mutable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool UnloadActiveContext()
    {
        var current = _activeContext;
        if (current is null)
            return false;
        _activeContext = null;
        var weak = UnloadAndGetWeakReference(current);
        for (var i = 0; i < 6 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return !weak.IsAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UnloadAndGetWeakReference(ActiveTestContext context)
    {
        var weak = new WeakReference(context.LoadContext, trackResurrection: true);
        context.LoadContext.Unload();
        return weak;
    }

    [RequiresUnreferencedCode("Attaches AssemblyDependencyResolver to Default AssemblyLoadContext.")]
    private void EnsureDefaultResolver(string testAssemblyPath)
    {
        lock (_resolverGate)
        {
            if (!string.IsNullOrWhiteSpace(testAssemblyPath)
                && File.Exists(testAssemblyPath)
                && !string.Equals(_resolverAssemblyPath, testAssemblyPath, StringComparison.OrdinalIgnoreCase))
            {
                _resolverAssemblyPath = testAssemblyPath;
                _resolver = new AssemblyDependencyResolver(testAssemblyPath);
            }

            if (_resolverAttached)
                return;
            _resolverAttached = true;

            AssemblyLoadContext.Default.Resolving += (defaultAlc, assemblyName) =>
            {
                if (string.IsNullOrEmpty(assemblyName.Name))
                    return null;
                // Mutable repository assemblies must never load into the default ALC.
                if (_mutableNames.Contains(assemblyName.Name))
                    return null;

                var resolver = _resolver;
                if (resolver?.ResolveAssemblyToPath(assemblyName) is { } resolved && File.Exists(resolved))
                    return defaultAlc.LoadFromAssemblyPath(resolved);

                var candidate = Path.Combine(_binDir, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                    return defaultAlc.LoadFromAssemblyPath(candidate);

                return ProbeSharedFrameworkAssembly(defaultAlc, assemblyName.Name);
            };

            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, unmanagedDllName) =>
            {
                var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
                return path is not null && File.Exists(path) ? System.Runtime.InteropServices.NativeLibrary.Load(path) : nint.Zero;
            };
        }
    }

    [RequiresUnreferencedCode("Loads shared framework assemblies dynamically.")]
    private static Assembly? ProbeSharedFrameworkAssembly(AssemblyLoadContext defaultAlc, string simpleName)
    {
        try
        {
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var sharedRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", ".."));
            foreach (var framework in new[] { "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App" })
            {
                var frameworkDir = Path.Combine(sharedRoot, framework);
                if (!Directory.Exists(frameworkDir))
                    continue;
                foreach (var versionDir in Directory.EnumerateDirectories(frameworkDir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(versionDir, simpleName + ".dll");
                    if (File.Exists(candidate))
                        return defaultAlc.LoadFromAssemblyPath(candidate);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
        }

        return null;
    }

    [RequiresUnreferencedCode("Inspects assembly references and custom attributes.")]
    private static string? CheckAssemblySafety(Assembly testAssembly)
    {
        foreach (var reference in testAssembly.GetReferencedAssemblies())
        {
            if (reference.Name is not null
                && (reference.Name.Equals("TUnit.Core", StringComparison.OrdinalIgnoreCase)
                    || reference.Name.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase)))
                return $"assembly references {reference.Name}";
        }

        foreach (var attr in testAssembly.GetCustomAttributesData())
        {
            var name = StripAttributeSuffix(attr.AttributeType.Name);
            if (name is "TestFramework" or "AssemblyFixture" or "SetUpFixture")
                return $"assembly declares [{name}]";
        }

        return null;
    }

    [RequiresUnreferencedCode("Discovers test types and methods via reflection.")]
    [RequiresDynamicCode("Creates arrays dynamically for theory attributes.")]
    private static List<DiscoveredTestCase> DiscoverTestCases(Assembly testAssembly, string[] selectedPatterns, out string? fallbackReason)
    {
        fallbackReason = null;
        var cases = new List<DiscoveredTestCase>();
        var collectionFixtures = new HashSet<string>(StringComparer.Ordinal);

        Type[] types;
        try
        {
            types = testAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            fallbackReason = $"could not load all types: {e.LoaderExceptions.FirstOrDefault()?.Message}";
            return [];
        }

        foreach (var type in types)
        {
            if (HasAttribute(type, "CollectionDefinition") && ImplementsFixtureInterface(type))
            {
                var collName = GetCollectionName(type);
                if (collName is not null)
                    collectionFixtures.Add(collName);
            }
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                continue;

            var className = FullTypeName(type);
            var candidateMethods = new List<(MethodInfo Method, string FullName, CustomAttributeData TestAttribute)>();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.DeclaringType == typeof(object))
                    continue;
                if (FindTestAttribute(method) is not { } testAttribute)
                    continue;
                var fullName = className + "." + method.Name;
                if (!MatchesPattern(fullName, method.Name, selectedPatterns))
                    continue;
                candidateMethods.Add((method, fullName, testAttribute));
            }

            if (candidateMethods.Count == 0)
                continue;

            if (CheckTypeSafety(type, collectionFixtures) is { } typeFallback)
            {
                fallbackReason = typeFallback;
                return [];
            }

            var setUp = FindLifecycleMethods(type, SetUpAttributes, baseFirst: true);
            var tearDown = FindLifecycleMethods(type, TearDownAttributes, baseFirst: false);

            foreach (var (method, fullName, testAttribute) in candidateMethods)
            {
                if (method.ContainsGenericParameters)
                {
                    fallbackReason = $"generic test method {fullName} requires full runner";
                    return [];
                }

                if (CheckMethodSafety(method, fullName) is { } methodFallback)
                {
                    fallbackReason = methodFallback;
                    return [];
                }

                var skipReason = GetSkipReason(type, method, testAttribute);
                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    cases.Add(new DiscoveredTestCase(type, method, fullName, null, skipReason, setUp, tearDown));
                    continue;
                }

                var rows = ExtractInlineDataRows(method, parameters);
                if (rows is null || rows.Count == 0)
                {
                    fallbackReason = $"parameterized test {fullName} uses unsupported or empty data source";
                    return [];
                }

                foreach (var row in rows)
                {
                    var display = $"{fullName}({string.Join(", ", row.Select(FormatArg))})";
                    cases.Add(new DiscoveredTestCase(type, method, display, row, skipReason, setUp, tearDown));
                }
            }
        }

        return cases;
    }

    [RequiresUnreferencedCode("Inspects type hierarchy and attributes.")]
    private static string? CheckTypeSafety(Type type, HashSet<string> collectionFixtures)
    {
        if (ImplementsFixtureInterface(type))
            return $"{type.Name} implements class or collection fixture";

        foreach (var attr in type.GetCustomAttributesData())
        {
            var name = StripAttributeSuffix(attr.AttributeType.Name);
            if (name is "Collection" && attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is string collName && collectionFixtures.Contains(collName))
                return $"{type.Name} uses collection fixture '{collName}'";
            if (UnsupportedLifecycleAttributes.Contains(name))
                return $"{type.Name} uses [{name}]";
        }

        var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.DefaultBinder, Type.EmptyTypes, null);
        if (ctor is null)
            return $"{type.Name} has no public parameterless constructor";

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var attr in method.GetCustomAttributesData())
            {
                var name = StripAttributeSuffix(attr.AttributeType.Name);
                if (UnsupportedLifecycleAttributes.Contains(name))
                    return $"{type.Name}.{method.Name} uses [{name}]";
            }
        }

        return null;
    }

    [RequiresUnreferencedCode("Inspects method attributes.")]
    private static string? CheckMethodSafety(MethodInfo method, string fullName)
    {
        foreach (var attr in method.GetCustomAttributesData())
        {
            var name = StripAttributeSuffix(attr.AttributeType.Name);
            if (UnsupportedDataAttributes.Contains(name))
                return $"{fullName} uses [{name}]";
            if (IsCustomDataAttribute(attr.AttributeType))
                return $"{fullName} uses custom data attribute [{name}]";
        }

        return null;
    }

    private static bool IsCustomDataAttribute(Type attributeType)
    {
        var name = StripAttributeSuffix(attributeType.Name);
        if (name is "InlineData" or "TestCase" or "DataRow")
            return false;
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            if (current.Name is "DataAttribute")
                return true;
        }

        return false;
    }

    private static bool ImplementsFixtureInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type) =>
        type.GetInterfaces().Any(i => i.Name.StartsWith("IClassFixture", StringComparison.Ordinal) || i.Name.StartsWith("ICollectionFixture", StringComparison.Ordinal));

    private static bool HasAttribute(Type type, string shortName) =>
        type.GetCustomAttributesData().Any(a => StripAttributeSuffix(a.AttributeType.Name) == shortName);

    private static string? GetCollectionName(Type type)
    {
        foreach (var attr in type.GetCustomAttributesData())
        {
            if (StripAttributeSuffix(attr.AttributeType.Name) == "CollectionDefinition" && attr.ConstructorArguments.Count > 0)
                return attr.ConstructorArguments[0].Value as string;
        }

        return null;
    }

    private static CustomAttributeData? FindTestAttribute(MethodInfo method)
    {
        foreach (var attr in method.GetCustomAttributesData())
        {
            for (var t = attr.AttributeType; t is not null; t = t.BaseType)
            {
                if (SupportedTestAttributes.Contains(StripAttributeSuffix(t.Name)))
                    return attr;
            }
        }

        return null;
    }

    private static string? GetSkipReason(Type type, MethodInfo method, CustomAttributeData testAttr)
    {
        foreach (var named in testAttr.NamedArguments)
        {
            if (named.MemberName is "Skip" or "Ignore" && named.TypedValue.Value is string s && !string.IsNullOrEmpty(s))
                return s;
        }

        foreach (var attr in method.GetCustomAttributesData().Concat(type.GetCustomAttributesData()))
        {
            if (StripAttributeSuffix(attr.AttributeType.Name) == "Ignore")
                return attr.ConstructorArguments.FirstOrDefault().Value as string ?? "ignored";
        }

        return null;
    }

    [RequiresDynamicCode("Creates arrays dynamically for theory attributes.")]
    private static List<object?[]>? ExtractInlineDataRows(MethodInfo method, ParameterInfo[] parameters)
    {
        var rows = new List<object?[]>();
        foreach (var attr in method.GetCustomAttributesData())
        {
            var shortName = StripAttributeSuffix(attr.AttributeType.Name);
            if (shortName is not ("InlineData" or "TestCase" or "DataRow"))
                continue;
            if (attr.ConstructorArguments.Count == 0)
                continue;

            object?[] rawValues;
            var first = attr.ConstructorArguments[0];
            if (shortName == "InlineData" && first.Value is IReadOnlyCollection<CustomAttributeTypedArgument> paramsArray)
                rawValues = [.. paramsArray.Select(UnwrapTypedArg)];
            else if (attr.ConstructorArguments.Count == 1 && first.Value is IReadOnlyCollection<CustomAttributeTypedArgument> arrayArg && parameters.Length != 1)
                rawValues = [.. arrayArg.Select(UnwrapTypedArg)];
            else if (attr.ConstructorArguments.Count == 2 && shortName == "DataRow" && attr.ConstructorArguments[1].Value is IReadOnlyCollection<CustomAttributeTypedArgument> additional)
                rawValues = [UnwrapTypedArg(first), .. additional.Select(UnwrapTypedArg)];
            else
                rawValues = [.. attr.ConstructorArguments.Select(UnwrapTypedArg)];

            if (rawValues.Length > parameters.Length)
                return null;
            var finalArgs = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i < rawValues.Length)
                    finalArgs[i] = rawValues[i];
                else if (parameters[i].HasDefaultValue)
                    finalArgs[i] = parameters[i].DefaultValue;
                else
                    return null;
            }

            rows.Add(finalArgs);
        }

        return rows;
    }

    [RequiresDynamicCode("Creates arrays dynamically for theory attributes.")]
    private static object? UnwrapTypedArg(CustomAttributeTypedArgument arg)
    {
        if (arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> nested)
        {
            var elemType = arg.ArgumentType.GetElementType() ?? typeof(object);
            var arr = Array.CreateInstance(elemType, nested.Count);
            var idx = 0;
            foreach (var item in nested)
                arr.SetValue(UnwrapTypedArg(item), idx++);
            return arr;
        }

        return arg.ArgumentType.IsEnum && arg.Value is not null
            ? Enum.ToObject(arg.ArgumentType, arg.Value)
            : arg.Value;
    }

    [RequiresUnreferencedCode("Finds setup/teardown lifecycle methods on test class.")]
    private static List<MethodInfo> FindLifecycleMethods(Type type, HashSet<string> attributeNames, bool baseFirst)
    {
        var hierarchy = new List<Type>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            hierarchy.Add(current);
        if (baseFirst)
            hierarchy.Reverse();

        var found = new List<MethodInfo>();
        foreach (var t in hierarchy)
        {
            foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetParameters().Length == 0
                    && method.GetCustomAttributesData().Any(a => attributeNames.Contains(StripAttributeSuffix(a.AttributeType.Name))))
                    found.Add(method);
            }
        }

        return found;
    }

    [RequiresUnreferencedCode("Invokes discovered test methods and collects IL/JIT coverage.")]
    private static async Task<(int Passed, int Failed, int Skipped, bool StoppedEarly)> ExecuteCasesAsync(
        ActiveTestContext context,
        List<DiscoveredTestCase> cases,
        WorkerCommand command,
        string repoRoot,
        Stream pipe,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var stoppedEarly = false;
        var mutableSet = new HashSet<Assembly>(context.LoadedAssemblies.Values);
        var mutableTypes = CollectMutableTypeNames(mutableSet);

        using var jitListener = command.CollectCoverage ? new JitCoverageListener(mutableTypes) : null;
        var pdbDocuments = command.CollectCoverage ? ReadPdbSourceMap(context.RawPayloads) : null;

        foreach (var testCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (testCase.SkipReason is not null)
            {
                skipped++;
                await SendEventAsync(pipe, writeLock, new WorkerEvent
                {
                    Kind = "verdict",
                    RunId = command.RunId,
                    TestName = testCase.DisplayName,
                    Verdict = "Skipped",
                    Message = testCase.SkipReason,
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            jitListener?.BeginTest();
            var timer = Stopwatch.StartNew();
            var (verdict, message, frames) = await InvokeSingleCaseAsync(testCase, repoRoot).ConfigureAwait(false);
            timer.Stop();
            var jitMethods = jitListener?.EndTest();

            switch (verdict)
            {
                case "Passed":
                    passed++;
                    break;
                case "Skipped":
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }

            await SendEventAsync(pipe, writeLock, new WorkerEvent
            {
                Kind = "verdict",
                RunId = command.RunId,
                TestName = testCase.DisplayName,
                Verdict = verdict,
                DurationMs = timer.Elapsed.TotalMilliseconds,
                Message = message,
                Frames = frames,
            }, cancellationToken).ConfigureAwait(false);

            if (command.CollectCoverage)
            {
                var (coveredMethods, coveredTypes) = CollectCoverage(testCase.Method, mutableSet, jitMethods, pdbDocuments);
                if (coveredMethods.Count > 0 || coveredTypes.Count > 0)
                {
                    await SendEventAsync(pipe, writeLock, new WorkerEvent
                    {
                        Kind = "coverage",
                        RunId = command.RunId,
                        TestName = MethodIdentity(testCase.Method),
                        CoveredMethods = [.. coveredMethods],
                        CoveredTypes = [.. coveredTypes],
                    }, cancellationToken).ConfigureAwait(false);
                }
            }

            if (command.MaxFailures > 0 && failed >= command.MaxFailures)
            {
                stoppedEarly = true;
                break;
            }
        }

        return (passed, failed, skipped, stoppedEarly);
    }

    [RequiresUnreferencedCode("Creates test class instances and invokes lifecycle and test methods.")]
    private static async Task<(string Verdict, string? Message, string[]? Frames)> InvokeSingleCaseAsync(DiscoveredTestCase testCase, string repoRoot)
    {
        object? instance = null;
        try
        {
            if (!testCase.Method.IsStatic)
                instance = Activator.CreateInstance(testCase.TestClass);

            if (instance is not null && FindInterfaceMethod(testCase.TestClass, "IAsyncLifetime", "InitializeAsync") is { } asyncInit)
                await AwaitIfAsync(asyncInit.Invoke(instance, null)).ConfigureAwait(false);

            foreach (var setUp in testCase.SetUp)
                await AwaitIfAsync(setUp.Invoke(instance, null)).ConfigureAwait(false);

            await AwaitIfAsync(testCase.Method.Invoke(instance, testCase.Arguments)).ConfigureAwait(false);
            return ("Passed", null, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is { } inner)
        {
            if (IsSkipException(inner))
                return ("Skipped", inner.Message, null);
            var frames = string.IsNullOrEmpty(repoRoot) ? Array.Empty<string>() : [.. TrxReader.Frames(inner.ToString(), repoRoot)];
            return ("Failed", TrxReader.Trim(inner.Message, 12), frames);
        }
        catch (Exception ex)
        {
            if (IsSkipException(ex))
                return ("Skipped", ex.Message, null);
            var frames = string.IsNullOrEmpty(repoRoot) ? Array.Empty<string>() : [.. TrxReader.Frames(ex.ToString(), repoRoot)];
            return ("Failed", TrxReader.Trim(ex.Message, 12), frames);
        }
        finally
        {
            if (instance is not null)
            {
                foreach (var tearDown in testCase.TearDown)
                {
                    try
                    {
                        await AwaitIfAsync(tearDown.Invoke(instance, null)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                try
                {
                    if (FindInterfaceMethod(testCase.TestClass, "IAsyncLifetime", "DisposeAsync") is { } asyncLifetimeDispose)
                        await AwaitIfAsync(asyncLifetimeDispose.Invoke(instance, null)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (instance is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (instance is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static async Task AwaitIfAsync(object? result)
    {
        switch (result)
        {
            case Task task:
                await task.ConfigureAwait(false);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                break;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported; inspects test class interfaces dynamically.")]
    private static MethodInfo? FindInterfaceMethod(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces | DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string interfaceName,
        string methodName)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.Name.Equals(interfaceName, StringComparison.Ordinal))
                return iface.GetMethod(methodName, Type.EmptyTypes) ?? type.GetMethod(methodName, Type.EmptyTypes);
        }

        return null;
    }

    private static bool IsSkipException(Exception ex)
    {
        var name = ex.GetType().Name;
        return name.EndsWith("SkipException", StringComparison.Ordinal)
            || name.EndsWith("IgnoreException", StringComparison.Ordinal)
            || name.EndsWith("InconclusiveException", StringComparison.Ordinal);
    }

    private static bool MatchesPattern(string fullName, string methodName, string[] patterns)
    {
        if (patterns.Length == 0)
            return true;
        foreach (var pattern in patterns)
        {
            if (pattern.EndsWith('.'))
            {
                if (fullName.StartsWith(pattern, StringComparison.Ordinal))
                    return true;
            }
            else if (string.Equals(fullName, pattern, StringComparison.Ordinal)
                     || (!pattern.Contains('.') && string.Equals(methodName, pattern, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    [RequiresUnreferencedCode("Walks IL of loaded mutable assemblies for coverage.")]
    private static (HashSet<string> Methods, HashSet<string> Types) CollectCoverage(
        MethodInfo entryMethod,
        HashSet<Assembly> mutableAssemblies,
        List<string>? jitMethods,
        Dictionary<string, HashSet<string>>? pdbDocuments)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();
        queue.Enqueue(entryMethod);

        // Index interface/base implementations across mutable assemblies for virtual dispatch resolution.
        var implementations = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal);
        foreach (var asm in mutableAssemblies)
        {
            Type[] asmTypes;
            try
            {
                asmTypes = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                continue;
            }

            foreach (var t in asmTypes)
            {
                if (t.IsInterface || t.IsAbstract)
                    continue;
                foreach (var iface in t.GetInterfaces())
                {
                    if (!mutableAssemblies.Contains(iface.Assembly))
                        continue;
                    InterfaceMapping map;
                    try
                    {
                        map = t.GetInterfaceMap(iface);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        var key = MethodIdentity(map.InterfaceMethods[i]);
                        if (!implementations.TryGetValue(key, out var list))
                            implementations[key] = list = [];
                        list.Add(map.TargetMethods[i]);
                    }
                }
            }
        }

        const int maxWalkMethods = 512;
        while (queue.Count > 0 && visited.Count < maxWalkMethods)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;
            if (current.DeclaringType is { } declaring && mutableAssemblies.Contains(declaring.Assembly))
            {
                methods.Add(MethodIdentity(current));
                types.Add(FullTypeName(declaring));
            }

            foreach (var token in ExtractMethodTokens(current))
            {
                MethodBase? target;
                try
                {
                    var typeArgs = current.DeclaringType?.IsGenericType == true ? current.DeclaringType.GetGenericArguments() : [];
                    var methodArgs = current.IsGenericMethod ? current.GetGenericArguments() : [];
                    target = current.Module.ResolveMethod(token, typeArgs, methodArgs);
                }
                catch (Exception)
                {
                    continue;
                }

                if (target?.DeclaringType is null || !mutableAssemblies.Contains(target.DeclaringType.Assembly))
                    continue;

                queue.Enqueue(target);
                if (implementations.TryGetValue(MethodIdentity(target), out var impls))
                {
                    foreach (var impl in impls)
                        queue.Enqueue(impl);
                }
            }
        }

        if (jitMethods is not null)
        {
            foreach (var hit in jitMethods)
            {
                methods.Add(hit);
                var dot = hit.LastIndexOf('.');
                if (dot > 0)
                    types.Add(hit[..dot]);
            }
        }

        _ = pdbDocuments;
        return (methods, types);
    }

    [RequiresUnreferencedCode("Reads IL method body tokens from test assemblies.")]
    private static IEnumerable<int> ExtractMethodTokens(MethodBase method)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch (Exception)
        {
            yield break;
        }

        var il = body?.GetILAsByteArray();
        if (il is null || il.Length == 0)
            yield break;

        var offset = 0;
        while (offset < il.Length)
        {
            var code = il[offset++];
            OpCode op;
            if (code == 0xFE)
            {
                if (offset >= il.Length)
                    break;
                op = TwoByteOpCodes[il[offset++]];
            }
            else
            {
                op = OneByteOpCodes[code];
            }

            if (op.OperandType == OperandType.InlineMethod && offset + 4 <= il.Length)
            {
                yield return BitConverter.ToInt32(il, offset);
                offset += 4;
                continue;
            }

            if (op.OperandType == OperandType.InlineSwitch)
            {
                if (offset + 4 > il.Length)
                    break;
                var count = BitConverter.ToInt32(il, offset);
                offset += 4 + (Math.Max(0, count) * 4);
                continue;
            }

            offset += OperandSize(op.OperandType);
        }
    }

    private static int OperandSize(OperandType operand) => operand switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 0,
    };

    private static Dictionary<string, HashSet<string>> ReadPdbSourceMap(IReadOnlyDictionary<string, (byte[] Pe, byte[] Pdb)> payloads)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (_, (_, pdbBytes)) in payloads)
        {
            if (pdbBytes.Length == 0)
                continue;
            try
            {
                using var provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(pdbBytes));
                _ = provider.GetMetadataReader();
            }
            catch (BadImageFormatException)
            {
            }
        }

        return map;
    }

    private static string MethodIdentity(MethodBase method)
    {
        var type = method.DeclaringType is null ? "" : FullTypeName(method.DeclaringType);
        return type.Length == 0 ? method.Name : $"{type}.{method.Name}";
    }

    private static string FullTypeName(Type type)
    {
        var nesting = new Stack<string>();
        var current = type;
        while (current is not null)
        {
            var tick = current.Name.IndexOf('`');
            nesting.Push(tick >= 0 ? current.Name[..tick] : current.Name);
            current = current.DeclaringType;
        }

        return string.IsNullOrEmpty(type.Namespace) ? string.Join('+', nesting) : type.Namespace + "." + string.Join('+', nesting);
    }

    private static string StripAttributeSuffix(string name) =>
        name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

    private static string FormatArg(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? "",
    };

    private static async Task SendEventAsync(Stream pipe, SemaphoreSlim writeLock, WorkerEvent evt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(evt, WarmWorkerJson.Default.WorkerEvent);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task WatchParentAsync(int parentPid, CancellationTokenSource shutdown)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await shutdown.CancelAsync().ConfigureAwait(false);
    }

    private static OpCode[] BuildOneByteOpCodes()
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
                continue;
            var val = (ushort)op.Value;
            if (val < 256)
                table[val] = op;
        }

        return table;
    }

    private static OpCode[] BuildTwoByteOpCodes()
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
                continue;
            var val = (ushort)op.Value;
            if ((val & 0xFF00) == 0xFE00)
                table[val & 0xFF] = op;
        }

        return table;
    }

    private sealed class ActiveTestContext
    {
        public ActiveTestContext(
            CollectibleTestLoadContext loadContext,
            Assembly? testAssembly,
            IReadOnlyDictionary<string, Assembly> loadedAssemblies,
            Dictionary<string, (byte[] Pe, byte[] Pdb)> rawPayloads)
        {
            LoadContext = loadContext;
            TestAssembly = testAssembly;
            LoadedAssemblies = loadedAssemblies;
            RawPayloads = rawPayloads;
        }

        public CollectibleTestLoadContext LoadContext { get; }

        public Assembly? TestAssembly { get; }

        public IReadOnlyDictionary<string, Assembly> LoadedAssemblies { get; }

        public Dictionary<string, (byte[] Pe, byte[] Pdb)> RawPayloads { get; }
    }

    private sealed class CollectibleTestLoadContext : AssemblyLoadContext
    {
        private readonly string _binDir;
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly Dictionary<string, (byte[] Pe, byte[] Pdb)> _mutable;
        private readonly ConcurrentDictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public CollectibleTestLoadContext(
            string binDir,
            AssemblyDependencyResolver? resolver,
            Dictionary<string, (byte[] Pe, byte[] Pdb)> mutable)
            : base(isCollectible: true)
        {
            _binDir = binDir;
            _resolver = resolver;
            _mutable = mutable;
        }

        public IReadOnlyDictionary<string, Assembly> LoadedAssemblies => _loaded;

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "CollectibleTestLoadContext is instantiated only when RuntimeFeature.IsDynamicCodeSupported is true.")]
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName.Name))
                return null;
            if (_loaded.TryGetValue(assemblyName.Name, out var existing))
                return existing;
            if (_mutable.TryGetValue(assemblyName.Name, out var image))
            {
                using var peStream = new MemoryStream(image.Pe, writable: false);
                using var pdbStream = image.Pdb.Length > 0 ? new MemoryStream(image.Pdb, writable: false) : null;
                var loaded = pdbStream is not null
                    ? LoadFromStream(peStream, pdbStream)
                    : LoadFromStream(peStream);
                _loaded[assemblyName.Name] = loaded;
                return loaded;
            }

            // Delegate non-mutable framework and NuGet assemblies to AssemblyLoadContext.Default so they stay JITted.
            return null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var resolved = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (resolved is not null && File.Exists(resolved))
                return LoadUnmanagedDllFromPath(resolved);
            var candidate = Path.Combine(_binDir, unmanagedDllName);
            return File.Exists(candidate) ? LoadUnmanagedDllFromPath(candidate) : nint.Zero;
        }
    }

    [RequiresUnreferencedCode("Collects mutable type names for JIT coverage filtering.")]
    private static HashSet<string> CollectMutableTypeNames(HashSet<Assembly> mutableAssemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in mutableAssemblies)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = [.. e.Types.OfType<Type>()];
            }

            foreach (var type in types)
                names.Add(FullTypeName(type));
        }

        return names;
    }

    private sealed class JitCoverageListener : EventListener
    {
        private readonly HashSet<string> _mutableTypes;
        private readonly ConcurrentQueue<string> _hits = new();
        private volatile bool _capturing;

        public JitCoverageListener(HashSet<string> mutableTypes) => _mutableTypes = mutableTypes;

        public void BeginTest()
        {
            while (_hits.TryDequeue(out _))
            {
            }

            _capturing = true;
        }

        public List<string> EndTest()
        {
            _capturing = false;
            var result = new List<string>();
            while (_hits.TryDequeue(out var hit))
                result.Add(hit);
            return result;
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!_capturing || eventData.Payload is null || eventData.PayloadNames is null)
                return;
            if (eventData.EventName is not ("MethodLoadVerbose_V2" or "MethodJittingStarted" or "MethodLoadVerbose_V1"))
                return;

            string? ns = null;
            string? methodName = null;
            for (var i = 0; i < eventData.PayloadNames.Count && i < eventData.Payload.Count; i++)
            {
                switch (eventData.PayloadNames[i])
                {
                    case "MethodNamespace":
                        ns = eventData.Payload[i] as string;
                        break;
                    case "MethodName":
                        methodName = eventData.Payload[i] as string;
                        break;
                }
            }

            if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(methodName))
                return;
            if (_mutableTypes.Contains(ns))
                _hits.Enqueue($"{ns}.{methodName}");
        }
    }

    private sealed record DiscoveredTestCase(
        Type TestClass,
        MethodInfo Method,
        string DisplayName,
        object?[]? Arguments,
        string? SkipReason,
        List<MethodInfo> SetUp,
        List<MethodInfo> TearDown);

    internal sealed class WorkerCommand
    {
        public string Kind { get; set; } = "";
        public string RunId { get; set; } = "";
        public string BinDirectory { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string TestAssemblyPath { get; set; } = "";
        public string TestAssemblySimpleName { get; set; } = "";
        public string? RepoRoot { get; set; }
        public WorkerAssemblyPayload[]? Assemblies { get; set; }
        public WorkerEncDelta[]? EncUpdates { get; set; }
        public string[] SelectedPatterns { get; set; } = [];
        public int MaxFailures { get; set; } = 10;
        public bool CollectCoverage { get; set; }
        public bool KeepAliveForEnc { get; set; } = true;
    }

    internal sealed class WorkerAssemblyPayload
    {
        public string SimpleName { get; set; } = "";
        public string PeBase64 { get; set; } = "";
        public string PdbBase64 { get; set; } = "";
    }

    internal sealed class WorkerEncDelta
    {
        public string SimpleName { get; set; } = "";
        public string MetadataBase64 { get; set; } = "";
        public string IlBase64 { get; set; } = "";
        public string PdbBase64 { get; set; } = "";
    }

    internal sealed class WorkerEvent
    {
        public string Kind { get; set; } = "";
        public int ProcessId { get; set; }
        public bool SupportsMetadataUpdate { get; set; }
        public string? RunId { get; set; }
        public string? TestName { get; set; }
        public string? Verdict { get; set; }
        public double DurationMs { get; set; }
        public string? Message { get; set; }
        public string[]? Frames { get; set; }
        public string[]? CoveredMethods { get; set; }
        public string[]? CoveredTypes { get; set; }
        public string? Reason { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public bool EncApplied { get; set; }
        public bool StoppedEarly { get; set; }
        public bool AlcUnloaded { get; set; }
        public double TotalMs { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(WorkerCommand))]
    [JsonSerializable(typeof(WorkerEvent))]
    internal sealed partial class WarmWorkerJson : JsonSerializerContext;
}
