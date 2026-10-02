namespace Fuse.Testing.Model;

/// <summary>How far a selection's filter was cut back to stay within the bound on its patterns.</summary>
internal enum FilterCollapse
{
    /// <summary>The filter has one pattern per pattern of the selection.</summary>
    None,

    /// <summary>The selection had too many patterns, so the filter has one class prefix per test class it names.</summary>
    ToClasses,

    /// <summary>The selection named too many test classes, so there is no filter and the project runs whole.</summary>
    ToWholeProject,
}
