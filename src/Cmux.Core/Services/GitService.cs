using System.Diagnostics;

namespace Cmux.Core.Services;

/// <summary>
/// Extracts git information (branch, remote) for a working directory.
/// Reads .git/HEAD directly when possible and only spawns git.exe as a fallback.
/// </summary>
public static class GitService
{
    private static readonly TimeSpan NoRepoCacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCachedNoRepoDirectories = 256;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, DateTime> NoRepoUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    // Resolved once from PATH; a full path avoids CreateProcess's search order (current directory first).
    private static readonly Lazy<string?> GitExecutable = new(FindGitExecutable);

    /// <summary>
    /// Gets the current git branch name for the given directory.
    /// Returns null if not a git repository, or for UNC (\\server\share) paths, which are never probed.
    /// </summary>
    public static string? GetBranch(string? workingDirectory)
    {
        if (!IsLocalFullyQualifiedPath(workingDirectory) || IsKnownNonRepo(workingDirectory!))
            return null;

        var gitHeadPath = FindGitHead(workingDirectory!);
        if (gitHeadPath == null)
        {
            RememberNonRepo(workingDirectory!);
            return null;
        }

        if (File.Exists(gitHeadPath))
        {
            try
            {
                var content = File.ReadAllText(gitHeadPath).Trim();
                const string refPrefix = "ref: refs/heads/";
                if (content.StartsWith(refPrefix, StringComparison.Ordinal))
                    return content[refPrefix.Length..];

                // Detached HEAD: return the short SHA.
                if (content.Length >= 7)
                    return content[..7];
            }
            catch
            {
                // Fall through to git
            }
        }

        return RunGit("rev-parse --abbrev-ref HEAD", workingDirectory!);
    }

    /// <summary>
    /// Gets the git remote URL for the given directory.
    /// </summary>
    public static string? GetRemoteUrl(string? workingDirectory)
    {
        if (!IsLocalFullyQualifiedPath(workingDirectory))
            return null;
        return RunGit("config --get remote.origin.url", workingDirectory!);
    }

    /// <summary>
    /// True for absolute local paths. False for UNC (\\server\share, //server/share), device paths
    /// (\\.\), and relative paths. Checked before any filesystem access.
    /// </summary>
    internal static bool IsLocalFullyQualifiedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var p = path;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..]; // long-path prefix; \\?\UNC\ is still UNC and caught below

        if (p.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(@"\\", StringComparison.Ordinal) ||
            p.StartsWith("//", StringComparison.Ordinal))
            return false;

        return Path.IsPathFullyQualified(p);
    }

    private static bool IsKnownNonRepo(string directory)
    {
        lock (CacheGate)
        {
            if (!NoRepoUntilUtc.TryGetValue(directory, out var until))
                return false;

            if (DateTime.UtcNow < until)
                return true;

            NoRepoUntilUtc.Remove(directory);
            return false;
        }
    }

    private static void RememberNonRepo(string directory)
    {
        lock (CacheGate)
        {
            if (NoRepoUntilUtc.Count >= MaxCachedNoRepoDirectories)
            {
                var now = DateTime.UtcNow;
                foreach (var key in NoRepoUntilUtc.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                    NoRepoUntilUtc.Remove(key);
                if (NoRepoUntilUtc.Count >= MaxCachedNoRepoDirectories)
                    NoRepoUntilUtc.Clear();
            }

            NoRepoUntilUtc[directory] = DateTime.UtcNow + NoRepoCacheTtl;
        }
    }

    private static string? FindGitHead(string directory)
    {
        var current = directory;
        while (current != null)
        {
            var gitDir = Path.Combine(current, ".git");
            if (Directory.Exists(gitDir))
                return Path.Combine(gitDir, "HEAD");

            // Handle .git file (worktrees and submodules)
            if (File.Exists(gitDir))
            {
                try
                {
                    var content = File.ReadAllText(gitDir).Trim();
                    if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
                    {
                        var gitDirPath = content["gitdir: ".Length..];
                        if (!Path.IsPathRooted(gitDirPath))
                            gitDirPath = Path.GetFullPath(Path.Combine(current, gitDirPath));
                        return Path.Combine(gitDirPath, "HEAD");
                    }
                }
                catch
                {
                    // Continue upward
                }
            }

            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static string? FindGitExecutable()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim('"'), "git.exe");
                // Only absolute PATH entries: a relative entry would resolve against the working directory.
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // No git available
        }
        return null;
    }

    private static string? RunGit(string arguments, string workingDirectory)
    {
        var exe = GitExecutable.Value;
        if (exe == null)
            return null;

        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // don't take index locks for a status read
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // drain so git never blocks on a full pipe

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var output = stdout.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
