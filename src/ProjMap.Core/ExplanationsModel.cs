namespace ProjMap.Core;

/// <summary>Optional, deterministic Russian explanations. Confidence belongs to each claim, never the node.</summary>
public sealed record ExplanationsInfo
{
    public int SchemaVersion { get; init; } = 1;
    public string Language { get; init; } = "ru";
    public string RuleSet { get; init; } = "builtin:explanations-v1";
    public List<NodeExplanation> Nodes { get; init; } = [];
    /// Shared sources; claim sourceIds are indexes into this array (not graph node indexes).
    public List<ExplanationSource> Sources { get; init; } = [];
    /// Case-folded Russian/original names, identifier words, paths and UI labels → indexes into Nodes.
    public Dictionary<string, List<int>> NameIndex { get; init; } = new(StringComparer.Ordinal);
}

public sealed record NodeExplanation
{
    public required string NodeId { get; init; }
    public required ExplanationClaim DisplayName { get; init; }
    public required ExplanationClaim Description { get; init; }
    public List<ExplanationClaim>? Claims { get; init; }
}

public sealed record ExplanationClaim(string Text, string Confidence, List<int> SourceIds);

public static class ExplanationConfidence
{
    public const string Known = "known";
    public const string Derived = "derived";
    public const string Guessed = "guessed";
}

/// <summary>Kind distinguishes author assertions from code observations and owner corrections.
/// Reference points to existing tier/feature/timeline data rather than copying that data.</summary>
public sealed record ExplanationSource
{
    public required string Kind { get; init; }
    public string? Path { get; init; }
    public int? Line { get; init; }
    public int? EndLine { get; init; }
    public string? SourceHash { get; init; }
    public string? CommitId { get; init; }
    public string? RuleId { get; init; }
    public string? Reference { get; init; }
    public string? Text { get; init; }
}

public sealed record EntryPointInfo
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public int Line { get; init; }
    public int? EndLine { get; init; }
    public string? Anchor { get; init; }
    public string? ControlName { get; init; }
    public string? Label { get; init; }
    public int? LabelLine { get; init; }
    /// literal | expression; computed text is never presented as a visible literal.
    public string? LabelKind { get; init; }
    public string? Event { get; init; }
    public required string Detection { get; init; }
    public List<string> HandlerIds { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ambiguous { get; init; }
}

/// <summary>Exact node for ordinary declarations; handlers require an unambiguous anchor, so ordinal changes
/// cannot silently transfer the correction to another callback. Owner entries follow project entries.</summary>
public sealed record ExplanationOverride
{
    public string? NodeId { get; init; }
    public string? Anchor { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}
