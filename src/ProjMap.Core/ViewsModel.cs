namespace ProjMap.Core;

/// <summary>
/// Optional `views` section of graph.json: a projection layer over the same nodes (see docs/GRAPH_CONTRACT.md §Views).
/// Facts of the scan (nodes, edges, ParentId tree) are never changed by it; a viewer without views support ignores it.
///  - Tier assignments are per file node, with a score, a decision and the evidence that produced them.
///  - Features ("функции") are named groups of nodes that cut across tiers and modules; one node may be in several.
/// </summary>
public sealed record ViewsInfo
{
    public int SchemaVersion { get; init; } = 1;
    /// "builtin:tiers-v1", plus "+.projmap/views.rules.json" and/or "+owner" when those contributed.
    public required string RuleSet { get; init; }
    public List<TierInfo> Tiers { get; init; } = [];
    public List<TierAssignment> Assignments { get; init; } = [];
    public List<FeatureInfo> Features { get; init; } = [];
    public List<FeatureMembership> Memberships { get; init; } = [];
    /// Manual corrections that no longer match anything (renamed/deleted node, unknown feature). Never re-targeted silently.
    public List<string> StaleOverrides { get; init; } = [];
}

public sealed record TierInfo(string Id, string Name, string Description);

public static class Tiers
{
    public const string Frontend = "frontend";
    public const string Application = "application";
    public const string Domain = "domain";
    public const string Data = "data";
    public const string Backend = "backend";
    public const string Infrastructure = "infrastructure";
    public const string Tests = "tests";
    public const string Docs = "docs";
    /// Two strong, close, contradicting signals: no winner is invented (see `Alternatives`).
    public const string Mixed = "mixed";
    /// No usable signal.
    public const string Unclassified = "unclassified";
}

public static class TierDecisions
{
    public const string Automatic = "automatic"; // score ≥ 0.75 and a clear margin over the next tier
    public const string Guess = "guess";         // only weak signals (path, names) or a small margin
    public const string Mixed = "mixed";
    public const string Unknown = "unknown";
    public const string Override = "override";   // set by the owner or by an override in the rules file
}

public sealed record TierAssignment
{
    public required string NodeId { get; init; }
    public required string Tier { get; init; }
    public string? Role { get; init; }
    /// desktop | browser | server | cli | shared | unknown
    public string? Execution { get; init; }
    public required string Decision { get; init; }
    public double Score { get; init; }
    /// One line in plain Russian: why this tier.
    public string? Reason { get; init; }
    public List<string>? Alternatives { get; init; }
    public List<TierEvidence> Evidence { get; init; } = [];
}

/// <param name="Family">syntax | rules | path | module | framework | override</param>
public sealed record TierEvidence(string Family, string Tier, double Score, string Rule, string Detail);

public static class FeatureStates
{
    public const string Candidate = "candidate"; // found automatically, not confirmed
    public const string Manual = "manual";       // added, renamed or merged by the owner or the rules file
}

public sealed record FeatureInfo
{
    /// "feature:<term>" for found features, the owner's id for added ones. Never derived from the display name.
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string State { get; init; } = FeatureStates.Candidate;
    /// Name terms the group was built from (lowercase stems).
    public List<string> Terms { get; init; } = [];
    public int Files { get; init; }
    public List<string> TierIds { get; init; } = [];
}

public static class FeatureRoles
{
    public const string Entry = "entry";                   // a button / handler that starts the function
    public const string Implementation = "implementation";
    public const string Test = "test";
    public const string Doc = "doc";
}

public sealed record FeatureMembership
{
    public required string FeatureId { get; init; }
    public required string NodeId { get; init; }
    public required string Role { get; init; }
    public double Score { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Manual corrections. The owner's live in the projmap AppStore (never in the scanned repository);
/// the same shape is accepted under `overrides` in `.projmap/views.rules.json`. Owner corrections win.</summary>
public sealed record ViewsOverrides
{
    public List<TierOverride> Tiers { get; init; } = [];
    /// feature id → display name
    public Dictionary<string, string> FeatureNames { get; init; } = new(StringComparer.Ordinal);
    public List<FeatureMerge> FeatureMerges { get; init; } = [];
    public List<string> HiddenFeatures { get; init; } = [];
    public List<FeatureDefinition> AddedFeatures { get; init; } = [];
    public List<FeatureMemberOverride> Members { get; init; } = [];
    public bool IsEmpty => Tiers.Count == 0 && FeatureNames.Count == 0 && FeatureMerges.Count == 0 &&
        HiddenFeatures.Count == 0 && AddedFeatures.Count == 0 && Members.Count == 0;
}

/// <summary>Exactly one of <see cref="NodeId"/> (exact) or <see cref="PathGlob"/> selects the files.</summary>
public sealed record TierOverride
{
    public string? NodeId { get; init; }
    public string? PathGlob { get; init; }
    public required string Tier { get; init; }
    public string? Role { get; init; }
    public string? Execution { get; init; }
    public string? Reason { get; init; }
}

public sealed record FeatureMerge(string Into, List<string> From);

public sealed record FeatureDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<string> Terms { get; init; } = [];
    public List<string> PathGlobs { get; init; } = [];
}

public sealed record FeatureMemberOverride(string FeatureId, string NodeId, bool Include);
