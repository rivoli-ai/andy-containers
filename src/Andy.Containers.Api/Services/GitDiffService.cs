using System.Text;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <inheritdoc cref="IGitDiffService"/>
public sealed class GitDiffService : IGitDiffService
{
    private readonly ContainersDbContext _db;
    private readonly IContainerService _containerService;
    private readonly ILogger<GitDiffService> _logger;

    private static readonly TimeSpan DiffTimeout = TimeSpan.FromSeconds(30);

    public GitDiffService(
        ContainersDbContext db,
        IContainerService containerService,
        ILogger<GitDiffService> logger)
    {
        _db = db;
        _containerService = containerService;
        _logger = logger;
    }

    public async Task<GitDiffResult> GetDiffAsync(Guid containerId, Guid? repoId, CancellationToken ct = default)
    {
        var reposQuery = _db.ContainerGitRepositories
            .Where(r => r.ContainerId == containerId);
        if (repoId is { } rid)
            reposQuery = reposQuery.Where(r => r.Id == rid);

        var repos = await reposQuery.OrderBy(r => r.CreatedAt).ToListAsync(ct);

        // Resolve the run hosting this container (most-recent first) so we can
        // name the run branch + its base. The branch is also discoverable from
        // git itself, but the run row is the authoritative anchor (F6.1).
        // Order client-side: Run.CreatedAt is a DateTimeOffset, which SQLite
        // (used in embedded/bundled mode) can't ORDER BY in SQL.
        var run = (await _db.Runs
                .Where(r => r.ContainerId == containerId)
                .ToListAsync(ct))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();

        var runBranch = run?.WorkspaceRef?.Branch;

        // Workspace base branch (best-effort): used as the base when the repo
        // itself didn't pin one.
        string? workspaceBase = null;
        if (run is not null && run.WorkspaceRef?.WorkspaceId is { } wsId && wsId != Guid.Empty)
        {
            workspaceBase = await _db.Workspaces
                .Where(w => w.Id == wsId)
                .Select(w => w.GitBranch)
                .FirstOrDefaultAsync(ct);
        }

        var result = new GitDiffResult
        {
            RunBranch = runBranch,
            BaseBranch = workspaceBase,
        };

        // No cloned repo → empty-but-OK (not an error).
        var clonedRepos = repos.Where(r => r.CloneStatus == GitCloneStatus.Cloned).ToList();
        if (clonedRepos.Count == 0)
        {
            return result;
        }

        var multiRepo = clonedRepos.Count > 1;
        var files = new List<GitDiffFile>();
        var rawPatch = new StringBuilder();

        foreach (var repo in clonedRepos)
        {
            var baseBranch = !string.IsNullOrWhiteSpace(repo.Branch) ? repo.Branch : workspaceBase;
            // Default base branch fallback used inside the container script.
            var (numstat, patch) = await RunDiffAsync(containerId, repo.TargetPath, baseBranch, runBranch, ct);
            if (numstat is null && patch is null)
                continue; // not a git repo / detached / exec failed → skip, stays empty-OK

            var prefix = multiRepo ? repo.TargetPath.TrimEnd('/') + "/" : null;
            var parsed = GitDiffParser.Parse(numstat ?? string.Empty, patch ?? string.Empty, prefix);
            files.AddRange(parsed);
        }

        foreach (var f in files)
        {
            if (rawPatch.Length > 0) rawPatch.Append('\n');
            rawPatch.Append(f.Patch);
        }

        result.Files = files;
        result.RawPatch = rawPatch.ToString();
        return result;
    }

    /// <summary>
    /// Run the numstat + unified-patch commands for one repo. Returns
    /// (numstat, patch) raw text, or (null, null) when the path is not a git
    /// repo or the exec failed (caller treats that as empty-OK).
    /// </summary>
    private async Task<(string? Numstat, string? Patch)> RunDiffAsync(
        Guid containerId, string repoPath, string? baseBranch, string? runBranch, CancellationToken ct)
    {
        var quotedPath = GitCloneService.ShellQuote(repoPath);

        // Build the diff range. We diff the base (committed) against the
        // working tree (HEAD + uncommitted) by diffing the merge-base of
        // base..HEAD and then the working tree. Using `<base>` as the single
        // left-hand side captures both committed-on-run-branch and dirty
        // working-tree changes in one `git diff <base>` invocation. When no
        // base is known we fall back to diffing the working tree vs HEAD plus
        // unstaged, which still surfaces in-run edits.
        string baseRef = !string.IsNullOrWhiteSpace(baseBranch) ? GitCloneService.ShellQuote(baseBranch!) : "HEAD";

        // Guard: only proceed if this is a git work tree. `git diff` against a
        // missing base ref would error; we resolve the base, and if it can't
        // be resolved we degrade to a plain working-tree diff (`git diff` +
        // staged) so the call still succeeds.
        var script =
            $"set -e; " +
            $"if ! git -C {quotedPath} rev-parse --is-inside-work-tree >/dev/null 2>&1; then exit 3; fi; " +
            $"BASE={baseRef}; " +
            $"if ! git -C {quotedPath} rev-parse --verify --quiet \"$BASE\" >/dev/null 2>&1; then BASE=HEAD; fi; " +
            $"echo '---NUMSTAT---'; " +
            $"git -C {quotedPath} diff --numstat \"$BASE\" 2>/dev/null; " +
            $"echo '---PATCH---'; " +
            $"git -C {quotedPath} diff \"$BASE\" 2>/dev/null";

        try
        {
            var result = await _containerService.ExecAsync(containerId, script, DiffTimeout, ct);
            if (result.ExitCode == 3)
            {
                _logger.LogDebug("Diff: {Path} in container {ContainerId} is not a git work tree.", repoPath, containerId);
                return (null, null);
            }
            if (result.ExitCode != 0)
            {
                _logger.LogWarning("Diff exec failed for {Path} in container {ContainerId} (exit {Exit}): {Err}",
                    repoPath, containerId, result.ExitCode, result.StdErr);
                return (null, null);
            }

            var output = result.StdOut ?? string.Empty;
            var numIdx = output.IndexOf("---NUMSTAT---", StringComparison.Ordinal);
            var patchIdx = output.IndexOf("---PATCH---", StringComparison.Ordinal);
            if (numIdx < 0 || patchIdx < 0 || patchIdx < numIdx)
                return (string.Empty, string.Empty);

            var numstat = output[(numIdx + "---NUMSTAT---".Length)..patchIdx].Trim('\n', '\r');
            var patch = output[(patchIdx + "---PATCH---".Length)..].TrimStart('\n', '\r');
            return (numstat, patch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diff exec threw for {Path} in container {ContainerId}; treating as empty.",
                repoPath, containerId);
            return (null, null);
        }
    }
}

/// <summary>
/// Pure parser for <c>git diff --numstat</c> + the unified patch into a
/// per-file <see cref="GitDiffFile"/> list. Split out for unit testing
/// (no container / exec needed). Applies the 64 KiB-per-file truncation cap.
/// </summary>
public static class GitDiffParser
{
    public static IReadOnlyList<GitDiffFile> Parse(string numstat, string patch, string? pathPrefix = null)
    {
        var files = new Dictionary<string, GitDiffFile>(StringComparer.Ordinal);
        var order = new List<string>();

        // 1) numstat → additions/deletions + path. Lines: "<adds>\t<dels>\t<path>".
        //    Binary files report "-\t-\t<path>".
        foreach (var line in SplitLines(numstat))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var rawPath = parts[2].Trim();
            if (rawPath.Length == 0) continue;
            var path = Prefixed(rawPath, pathPrefix);

            int? adds = int.TryParse(parts[0], out var a) ? a : null;
            int? dels = int.TryParse(parts[1], out var d) ? d : null;

            if (!files.TryGetValue(path, out var f))
            {
                f = new GitDiffFile { Path = path };
                files[path] = f;
                order.Add(path);
            }
            f.Additions = adds;
            f.Deletions = dels;
        }

        // 2) Split the unified patch by "diff --git" headers → per-file patch.
        foreach (var (rawPath, body, changeType) in SplitPatch(patch))
        {
            var path = Prefixed(rawPath, pathPrefix);
            if (!files.TryGetValue(path, out var f))
            {
                f = new GitDiffFile { Path = path };
                files[path] = f;
                order.Add(path);
            }
            f.ChangeType = changeType;
            var (clipped, truncated) = Truncate(body);
            f.Patch = clipped;
            f.Truncated = truncated;
        }

        return order.Select(p => files[p]).ToList();
    }

    private static string Prefixed(string path, string? prefix)
        => string.IsNullOrEmpty(prefix) ? path : prefix + path;

    private static (string Clipped, bool Truncated) Truncate(string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        if (bytes <= IGitDiffService.MaxPatchBytesPerFile) return (body, false);

        // Clip on a char boundary at/under the byte cap.
        var sb = new StringBuilder();
        var running = 0;
        foreach (var ch in body)
        {
            var w = Encoding.UTF8.GetByteCount(new[] { ch });
            if (running + w > IGitDiffService.MaxPatchBytesPerFile) break;
            running += w;
            sb.Append(ch);
        }
        sb.Append("\n... [truncated: patch exceeded 64 KiB] ...\n");
        return (sb.ToString(), true);
    }

    private static IEnumerable<(string Path, string Body, string ChangeType)> SplitPatch(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) yield break;

        var lines = patch.Replace("\r\n", "\n").Split('\n');
        string? curPath = null;
        var bodyLines = new List<string>();
        var changeType = "modified";

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (curPath is not null)
                {
                    yield return (curPath, string.Join('\n', bodyLines), changeType);
                }
                bodyLines = new List<string> { line };
                changeType = "modified";
                curPath = ParseDiffGitPath(line);
            }
            else
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) changeType = "added";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) changeType = "deleted";
                else if (line.StartsWith("rename from", StringComparison.Ordinal) ||
                         line.StartsWith("rename to", StringComparison.Ordinal)) changeType = "renamed";

                // For renames/copies, the +++ b/ path is the most useful name.
                if (line.StartsWith("+++ b/", StringComparison.Ordinal) && curPath is not null)
                    curPath = line[6..].Trim();

                bodyLines.Add(line);
            }
        }

        if (curPath is not null)
        {
            yield return (curPath, string.Join('\n', bodyLines), changeType);
        }
    }

    private static string ParseDiffGitPath(string diffGitLine)
    {
        // "diff --git a/foo/bar.cs b/foo/bar.cs" → "foo/bar.cs"
        const string aMarker = " a/";
        var aIdx = diffGitLine.IndexOf(aMarker, StringComparison.Ordinal);
        if (aIdx >= 0)
        {
            var rest = diffGitLine[(aIdx + aMarker.Length)..];
            var bIdx = rest.IndexOf(" b/", StringComparison.Ordinal);
            if (bIdx >= 0) return rest[..bIdx].Trim();
            return rest.Trim();
        }
        return diffGitLine["diff --git ".Length..].Trim();
    }

    private static IEnumerable<string> SplitLines(string s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0) yield return line;
        }
    }
}
