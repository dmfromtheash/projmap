namespace ProjMap.Core;

/// <summary>Derived diagnostics, rebuilt after final views and metrics on every scan.</summary>
public sealed record ProblemsInfo
{
    public int SchemaVersion { get; init; } = 1;
    public int CycleLimit { get; init; } = 100;
    public int HotFileLimit { get; init; } = 20;
    /// Total candidates before the cycle/hot-file display caps, including zero counts.
    public Dictionary<string, int> Counts { get; init; } = new(StringComparer.Ordinal);
    public List<ProblemItem> Items { get; init; } = [];
}

public static class ProblemKinds
{
    public const string DeadCode = "deadCode";
    public const string ModuleCycle = "moduleCycle";
    public const string FileCycle = "fileCycle";
    public const string TierViolation = "tierViolation";
    public const string HotFile = "hotFile";
}

public sealed record ProblemItem
{
    public required string Kind { get; init; }
    public required List<string> NodeIds { get; init; }
    public required string Reason { get; init; }
    /// info | warn
    public required string Severity { get; init; }
    /// Builtin tier rule id, when applicable.
    public string? Rule { get; init; }
    /// Commits × complexity for hot files; 64-bit to avoid overflow.
    public long? Score { get; init; }
    /// Original edges behind a tier violation, with standalone, expanded evidence paths.
    public List<Edge>? Edges { get; init; }
}
