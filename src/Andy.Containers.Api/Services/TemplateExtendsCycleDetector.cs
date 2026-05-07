namespace Andy.Containers.Api.Services;

/// <summary>
/// Walks the <c>extends:</c> chain across templates and surfaces
/// cycles, missing parents, and overly long chains. Independent of
/// EF and YAML parsing so the registration pipeline can call it with
/// either an in-memory map (for batch validation of a YAML directory)
/// or a delegate that hits the templates table.
/// </summary>
/// <remarks>
/// IM4 (rivoli-ai/andy-containers#253). Detection runs at template
/// register-time — by the time a build is requested, the chain has
/// already been validated. Self-loops, two-cycles, and longer cycles
/// are all rejected with a descriptive message that lists the cycle
/// path so a typo is easy to find.
/// </remarks>
public static class TemplateExtendsCycleDetector
{
    /// <summary>
    /// Maximum allowed depth of the extends chain. Prevents
    /// pathological-but-technically-acyclic graphs from bogging down
    /// the registration pipeline. Picked at 16 because no realistic
    /// dev-container hierarchy goes deeper than a handful of levels.
    /// </summary>
    public const int MaxChainDepth = 16;

    /// <summary>
    /// Validate the extends chain starting from a template code. The
    /// resolver is called with each parent code in turn; it must
    /// distinguish "this template isn't registered" from "this
    /// template exists but doesn't extend anything." Both shapes
    /// are common during register-time validation: the former is
    /// a typo error, the latter is a clean chain endpoint.
    /// </summary>
    /// <param name="startCode">
    /// Code of the template whose chain is being validated. The
    /// caller is responsible for ensuring the start code exists —
    /// it's the one being registered.
    /// </param>
    /// <param name="extendsResolver">
    /// Maps a template code to a lookup result. Returns
    /// <see cref="ExtendsLookup.Missing"/> when the template isn't
    /// in the table at all; returns
    /// <see cref="ExtendsLookup.End"/> when the template exists but
    /// has no <c>extends:</c>; returns
    /// <see cref="ExtendsLookup.ExtendsCode"/> when the template
    /// extends another.
    /// </param>
    public static ExtendsResolutionResult Validate(
        string startCode,
        Func<string, ExtendsLookup> extendsResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startCode);
        ArgumentNullException.ThrowIfNull(extendsResolver);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var current = startCode;

        for (var depth = 0; depth <= MaxChainDepth; depth++)
        {
            // Self-loop detection. The first node is added to the
            // visited set before lookup so an A → A direct cycle
            // surfaces on the first iteration.
            if (!visited.Add(current))
            {
                path.Add(current);
                return ExtendsResolutionResult.Cycle(path);
            }
            path.Add(current);

            var lookup = extendsResolver(current);

            switch (lookup.Kind)
            {
                case ExtendsLookupKind.Missing:
                    // The starting code is owned by the caller — they
                    // know it exists. Missing for any *parent* code
                    // means the parent isn't registered.
                    if (current == startCode)
                    {
                        return ExtendsResolutionResult.MissingParent(current, path);
                    }
                    return ExtendsResolutionResult.MissingParent(current, path);

                case ExtendsLookupKind.End:
                    return ExtendsResolutionResult.Ok(path);

                case ExtendsLookupKind.Extends:
                    if (string.IsNullOrWhiteSpace(lookup.ExtendsCode))
                    {
                        return ExtendsResolutionResult.Ok(path);
                    }
                    current = lookup.ExtendsCode!;
                    break;
            }
        }

        // Walked past MaxChainDepth without finding a chain end.
        // Either an undetected cycle (defence-in-depth) or a real
        // overlong chain — surface both as a cycle so the operator
        // looks at the path.
        return ExtendsResolutionResult.TooDeep(path);
    }
}

/// <summary>
/// Result of looking up a template's <c>extends:</c> value during
/// chain walking.
/// </summary>
public readonly record struct ExtendsLookup(ExtendsLookupKind Kind, string? ExtendsCode)
{
    /// <summary>The template isn't in the registry — a typo.</summary>
    public static ExtendsLookup Missing => new(ExtendsLookupKind.Missing, null);

    /// <summary>The template exists but doesn't extend anything (chain root).</summary>
    public static ExtendsLookup End => new(ExtendsLookupKind.End, null);

    /// <summary>The template extends another template by code.</summary>
    public static ExtendsLookup Extends(string code) => new(ExtendsLookupKind.Extends, code);
}

public enum ExtendsLookupKind
{
    Missing,
    End,
    Extends,
}

/// <summary>
/// Outcome of an <see cref="TemplateExtendsCycleDetector.Validate"/>
/// call.
/// </summary>
public sealed record ExtendsResolutionResult
{
    public required ExtendsResolutionStatus Status { get; init; }

    /// <summary>
    /// The chain of template codes walked, in extends order
    /// (start → root). On <see cref="ExtendsResolutionStatus.Cycle"/>
    /// the last entry is the code where the cycle re-entered.
    /// </summary>
    public required IReadOnlyList<string> Path { get; init; }

    /// <summary>
    /// Code of the missing parent for
    /// <see cref="ExtendsResolutionStatus.MissingParent"/>; null
    /// otherwise.
    /// </summary>
    public string? MissingParentOf { get; init; }

    public bool IsValid => Status == ExtendsResolutionStatus.Ok;

    public string Describe() => Status switch
    {
        ExtendsResolutionStatus.Ok =>
            $"chain of {Path.Count}: {string.Join(" → ", Path)}",
        ExtendsResolutionStatus.Cycle =>
            $"cycle in extends chain: {string.Join(" → ", Path)}",
        ExtendsResolutionStatus.MissingParent =>
            $"template '{MissingParentOf}' references an extends parent that isn't registered. Path so far: {string.Join(" → ", Path)}",
        ExtendsResolutionStatus.TooDeep =>
            $"extends chain exceeds the maximum depth of {TemplateExtendsCycleDetector.MaxChainDepth}: {string.Join(" → ", Path)}",
        _ => $"unknown status {Status}",
    };

    internal static ExtendsResolutionResult Ok(IReadOnlyList<string> path)
        => new() { Status = ExtendsResolutionStatus.Ok, Path = path };

    internal static ExtendsResolutionResult Cycle(IReadOnlyList<string> path)
        => new() { Status = ExtendsResolutionStatus.Cycle, Path = path };

    internal static ExtendsResolutionResult MissingParent(string code, IReadOnlyList<string> path)
        => new() { Status = ExtendsResolutionStatus.MissingParent, Path = path, MissingParentOf = code };

    internal static ExtendsResolutionResult TooDeep(IReadOnlyList<string> path)
        => new() { Status = ExtendsResolutionStatus.TooDeep, Path = path };
}

public enum ExtendsResolutionStatus
{
    Ok,
    Cycle,
    MissingParent,
    TooDeep,
}
