namespace Andy.Containers.Models;

/// <summary>
/// Result of diffing a run's branch against its base inside a container.
/// Story F6.1 (rivoli-ai/conductor#1940). Produced by <c>IGitDiffService</c>
/// by running <c>git diff</c> through the infrastructure provider's exec
/// surface — the same path <c>GitCloneService</c> uses (ARCHITECTURE §16.3),
/// never a Docker-Engine verb (decision #17).
/// </summary>
/// <remarks>
/// Aggregates one or more repos. For a single-repo container the file paths
/// are repo-relative; for a multi-repo container the caller may scope to one
/// repo via <c>repoId</c>, otherwise every repo is aggregated and each file's
/// <see cref="GitDiffFile.Path"/> is prefixed with the repo's target path so
/// callers can disambiguate.
/// </remarks>
public class GitDiffResult
{
    /// <summary>The workspace base branch the run branched from (e.g. <c>main</c>).</summary>
    public string? BaseBranch { get; set; }

    /// <summary>The per-run branch (e.g. <c>andy/run/{runId}</c>).</summary>
    public string? RunBranch { get; set; }

    /// <summary>Per-file structured diff entries.</summary>
    public IReadOnlyList<GitDiffFile> Files { get; set; } = new List<GitDiffFile>();

    /// <summary>
    /// The concatenated raw unified patch across all included files, so
    /// callers can fall back to rendering the raw text. Each per-file patch
    /// is independently capped (see <see cref="GitDiffFile.Truncated"/>).
    /// </summary>
    public string RawPatch { get; set; } = string.Empty;
}

/// <summary>One file's change within a <see cref="GitDiffResult"/>.</summary>
public class GitDiffFile
{
    /// <summary>Path of the changed file, optionally repo-prefixed (multi-repo aggregate).</summary>
    public required string Path { get; set; }

    /// <summary>Change classification: <c>added</c>, <c>modified</c>, <c>deleted</c>, <c>renamed</c>.</summary>
    public string ChangeType { get; set; } = "modified";

    /// <summary>Added line count from <c>git diff --numstat</c>; null for binary files.</summary>
    public int? Additions { get; set; }

    /// <summary>Deleted line count from <c>git diff --numstat</c>; null for binary files.</summary>
    public int? Deletions { get; set; }

    /// <summary>The unified patch hunk for this file (possibly truncated).</summary>
    public string Patch { get; set; } = string.Empty;

    /// <summary>
    /// True when the per-file patch exceeded the 64 KiB cap and was
    /// truncated (mirrors the build-log cap). Avoids streaming megabyte
    /// blobs through the UnifiedProxy.
    /// </summary>
    public bool Truncated { get; set; }
}
