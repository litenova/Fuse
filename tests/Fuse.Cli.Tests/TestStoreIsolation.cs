using System.Runtime.CompilerServices;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Tests;

/// <summary>
///     Isolates the persistent Fuse store for the test process so tests never read or write the developer's real
///     machine-wide <c>~/.fuse</c> and never contend on a single shared database.
/// </summary>
/// <remarks>
///     <see cref="FuseStorePaths.ResolveDatabasePath" /> resolves <c>{repoRoot}/.fuse/fuse.db</c> when the source
///     directory sits inside a git work tree and the shared <c>~/.fuse/fuse.db</c> otherwise. A test whose fixture
///     is a bare temp directory therefore falls back to the shared store, so every such test hammers one real file
///     (locks, <c>index_busy</c>, and cross-test state accumulation). <see cref="AsIsolatedRepo" /> drops a
///     <c>.git</c> marker so the store resolves under the fixture instead, and <see cref="Initialize" /> redirects
///     the fallback to a throwaway directory as a safety net.
/// </remarks>
internal static class TestStoreIsolation
{
    /// <summary>
    ///     Runs once at assembly load, before any test: points the machine-wide store fallback at a throwaway
    ///     directory so temporary fixtures never touch a developer's derived index.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var userData = Path.Combine(Path.GetTempPath(), "fuse-tests-userdata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);
        Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, userData);
    }

    /// <summary>
    ///     Marks <paramref name="root" /> as a git work tree (an empty <c>.git</c> directory is all
    ///     <see cref="Fuse.Collection.FileSystem.RepositoryRootResolver" /> checks) so the Fuse store resolves to an
    ///     isolated <c>{root}/.fuse/fuse.db</c> rather than the shared machine-wide store. No git binary is required.
    /// </summary>
    /// <param name="root">The absolute fixture directory. Created if it does not exist.</param>
    /// <returns><paramref name="root" />, for call chaining.</returns>
    internal static string AsIsolatedRepo(this string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }
}
