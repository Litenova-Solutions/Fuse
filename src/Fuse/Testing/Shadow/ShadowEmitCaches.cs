using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing.Shadow;

/// <summary>What the emitters of one planner share, so an image one plan emits serves the next and concurrent plans agree on what each shadow file holds.</summary>
internal sealed class ShadowEmitCaches
{
    /// <summary>The emitted images kept between plans.</summary>
    internal EmitCache Cache { get; } = new();

    /// <summary>Each shadow file an emitter wrote, with its 32-byte SHA-256 hash and the length and time the file had after.</summary>
    internal ConcurrentDictionary<string, ShadowEmitter.Written> Written { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The written files in the order they were last written in, for <see cref="WrittenGate"/>.</summary>
    internal List<string> WrittenOrder { get; } = [];

    /// <summary>Guards <see cref="WrittenOrder"/>, the order the written files were last written in.</summary>
    internal Lock WrittenGate { get; } = new();

    /// <summary>For each dependent an emitter wrote, the declaration fingerprint of each project it references, as it was when the dependent was written.</summary>
    internal ConcurrentDictionary<ProjectId, Dictionary<ProjectId, string>> Fingerprints { get; } = new();
    /// <summary>The dependents in the order they were last written in, for <see cref="FingerprintGate"/>.</summary>
    internal List<ProjectId> FingerprintOrder { get; } = [];

    /// <summary>Guards <see cref="FingerprintOrder"/>, the order the dependents were last written in.</summary>
    internal Lock FingerprintGate { get; } = new();

    /// <summary>Serializes the choice of a shadow copy, so two plans never choose the copy the other is writing.</summary>
    internal Lock Allocation { get; } = new();

    /// <summary>The places in the order of plans and of the answers they gave taken so far, for <see cref="Answered"/>.</summary>
    private long _sequence;

    /// <summary>The next place in the order of plans and of the answers they gave.</summary>
    internal long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    ///     Each copy a plan answered with, with the place in the order its preparation ended. A copy stays
    ///     here until a plan that is not preparing it finds a run holding it, which says its answer was taken;
    ///     from then on a later plan may remove it once no run holds it.
    /// </summary>
    internal ConcurrentDictionary<string, long> Answered { get; } = new(StringComparer.OrdinalIgnoreCase);
}
