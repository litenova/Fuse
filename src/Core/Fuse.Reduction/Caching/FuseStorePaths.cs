using System.Security.Cryptography;
using System.Text;
using Fuse.Collection.FileSystem;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Resolves on-disk paths for the persistent Fuse workspace index.
/// </summary>
public static class FuseStorePaths
{
    /// <summary>
    ///     Environment variable overriding the machine-wide Fuse data directory (default
    ///     <c>~/.fuse</c>). Used for tests and custom install layouts.
    /// </summary>
    public const string UserDataEnvironmentVariable = "FUSE_USER_DATA";

    /// <summary>The semantic index database file name inside the Fuse data directory.</summary>
    public const string DatabaseFileName = "fuse.db";

    /// <summary>
    ///     The relative path from a git repository root to the semantic index database
    ///     (<c>.fuse/fuse.db</c>).
    /// </summary>
    public const string RepositoryDatabaseRelativePath = ".fuse/fuse.db";

    /// <summary>
    ///     Returns the machine-wide Fuse data directory (<c>~/.fuse</c> by default).
    /// </summary>
    public static string GetUserDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(UserDataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fuse");
    }

    /// <summary>
    ///     Resolves the absolute path to <c>fuse.db</c> for a fusion source directory.
    /// </summary>
    /// <param name="sourceDirectory">The fusion source directory.</param>
    /// <returns>
    ///     <c>{repoRoot}/.fuse/fuse.db</c> when inside a git repository; otherwise
    ///     <c>~/.fuse/fuse.db</c> (or <see cref="UserDataEnvironmentVariable" />).
    /// </returns>
    public static string ResolveDatabasePath(string sourceDirectory) =>
        ResolveStorePath(sourceDirectory, RepositoryDatabaseRelativePath, DatabaseFileName);

    private static string ResolveStorePath(
        string sourceDirectory,
        string repositoryRelativePath,
        string userDataFileName)
    {
        var repositoryRoot = RepositoryRootResolver.TryFindRepositoryRoot(sourceDirectory);
        if (repositoryRoot is not null)
        {
            return Path.Combine(
                repositoryRoot,
                repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        // R34: a non-git directory has no repository root to anchor its store, so it used to fall back to one
        // shared machine-wide fuse.db. Two unrelated non-git directories then collided on a single store,
        // polluting each other's results (a dogfood run mixed 32 MB of temp-path junk into the real store). Give
        // each non-git root its own store under {user-data}/roots/{hash}/, keyed by the normalized absolute root,
        // so non-git workspaces never share a store. FUSE_USER_DATA still redirects the base for tests.
        return Path.Combine(GetUserDataDirectory(), "roots", RootHash(sourceDirectory), userDataFileName);
    }

    /// <summary>
    ///     The per-root subdirectory name for a non-git directory's fallback store: a short, stable hex hash of the
    ///     normalized absolute root, so two distinct non-git roots never share a store (R34).
    /// </summary>
    /// <param name="sourceDirectory">The non-git source directory.</param>
    /// <returns>A stable 16-character hex hash.</returns>
    public static string RootHash(string sourceDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
