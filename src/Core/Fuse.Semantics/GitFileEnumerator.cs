using System.Diagnostics;

namespace Fuse.Semantics;

/// <summary>
///     Reads a Git working tree inventory with tracked blob ids and dirty-path state.
/// </summary>
/// <remarks>
///     A clean tracked file reuses its Git blob id as the index content identity. Dirty and untracked files must
///     be read and hashed from disk because the index does not match their working-tree bytes. Git failures return
///     null so callers can fall back to a filesystem inventory.
/// </remarks>
public sealed class GitFileEnumerator
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Lists the working-tree files as paths relative to <paramref name="rootDirectory" />.
    /// </summary>
    /// <param name="rootDirectory">The candidate repository root.</param>
    /// <param name="cancellationToken">A token to cancel the Git calls.</param>
    /// <returns>The relative file paths, or null when Git cannot describe the work tree.</returns>
    public async Task<IReadOnlyList<string>?> TryListAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var inventory = await TryDescribeAsync(rootDirectory, cancellationToken);
        return inventory?.Paths;
    }

    /// <summary>
    ///     Reads tracked blob ids from <c>git ls-files -s -z</c> and dirty paths from porcelain v2 status.
    /// </summary>
    /// <param name="rootDirectory">The candidate repository root.</param>
    /// <param name="cancellationToken">A token to cancel the Git calls.</param>
    /// <returns>The Git inventory, or null when Git is unavailable or does not describe this directory.</returns>
    public async Task<GitWorkspaceInventory?> TryDescribeAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var indexOutput = await RunGitAsync(
            root,
            ["-c", "core.quotepath=false", "ls-files", "-s", "-z"],
            cancellationToken);
        if (indexOutput is null)
            return null;

        var statusOutput = await RunGitAsync(
            root,
            ["-c", "core.quotepath=false", "status", "--porcelain=v2", "-z", "--untracked-files=all"],
            cancellationToken);
        if (statusOutput is null)
            return null;

        var tracked = ParseTracked(indexOutput);
        var dirty = ParseDirtyPaths(statusOutput);
        var paths = tracked.Keys
            .Concat(dirty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new GitWorkspaceInventory(paths, tracked, dirty);
    }

    private static IReadOnlyDictionary<string, string> ParseTracked(string output)
    {
        var tracked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0 || tab == entry.Length - 1)
                continue;

            var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !string.Equals(fields[2], "0", StringComparison.Ordinal))
                continue;

            tracked[entry[(tab + 1)..]] = fields[1];
        }

        return tracked;
    }

    private static IReadOnlySet<string> ParseDirtyPaths(string output)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var records = output.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 2)
                continue;

            switch (record[0])
            {
                case '1':
                    if (HasWorktreeChange(record))
                        AddPathAfterFields(record, 8, dirty);
                    break;
                case '2':
                    if (HasWorktreeChange(record))
                    {
                        AddPathAfterFields(record, 9, dirty);
                        if (index + 1 < records.Length && records[index + 1].Length > 0)
                            dirty.Add(records[++index]);
                    }
                    break;
                case 'u':
                    AddPathAfterFields(record, 10, dirty);
                    break;
                case '?':
                    if (record.Length > 2)
                        dirty.Add(record[2..]);
                    break;
            }
        }

        return dirty;
    }

    private static bool HasWorktreeChange(string record) =>
        record.Length < 4 || record[3] != '.';

    private static void AddPathAfterFields(string record, int fieldCount, ISet<string> paths)
    {
        var position = 0;
        for (var field = 0; field < fieldCount; field++)
        {
            position = record.IndexOf(' ', position);
            if (position < 0)
                return;
            position++;
        }

        if (position < record.Length)
            paths.Add(record[position..]);
    }

    private static async Task<string?> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";

        using var timeout = new CancellationTokenSource(GitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            await stderrTask;
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}

/// <summary>
///     The Git source inventory for one repository root.
/// </summary>
/// <param name="Paths">Tracked and working-tree paths that Git reports.</param>
/// <param name="TrackedBlobIds">The index blob id for each tracked path.</param>
/// <param name="DirtyPaths">Paths whose working-tree content differs from Git or is untracked.</param>
public sealed record GitWorkspaceInventory(
    IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, string> TrackedBlobIds,
    IReadOnlySet<string> DirtyPaths)
{
    /// <summary>
    ///     Returns the clean tracked blob id for a path, or null when the working tree must be hashed from disk.
    /// </summary>
    /// <param name="path">The normalized relative path.</param>
    /// <returns>The clean blob id, or null for dirty, untracked, or unknown files.</returns>
    public string? GetCleanBlobId(string path) =>
        !DirtyPaths.Contains(path) && TrackedBlobIds.TryGetValue(path, out var blobId) ? blobId : null;
}
