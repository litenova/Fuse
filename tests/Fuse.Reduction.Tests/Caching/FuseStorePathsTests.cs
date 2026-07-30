using Fuse.Reduction.Caching;

namespace Fuse.Reduction.Tests.Caching;

public sealed class FuseStorePathsTests
{
    [Fact]
    public void ResolveDatabasePath_InsideGitRepo_ReturnsRepositoryRelativePath()
    {
        var repo = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var nested = Path.Combine(repo, "src", "Api");
            Directory.CreateDirectory(nested);

            var expected = Path.Combine(repo, ".fuse", "fuse.db");
            Assert.Equal(Path.GetFullPath(expected), FuseStorePaths.ResolveDatabasePath(nested));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ResolveDatabasePath_NoGitDirectory_ReturnsPerRootUserDataPath()
    {
        // R34: a non-git directory gets its own per-root store under {user-data}/roots/{hash}/, not the shared
        // machine-wide fuse.db, so FUSE_USER_DATA still redirects the base while the root stays isolated.
        var root = CreateTempDirectory();
        var userData = Path.Combine(root, "user-data");
        Directory.CreateDirectory(userData);
        Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, userData);

        try
        {
            var nested = Path.Combine(root, "src", "Api");
            Directory.CreateDirectory(nested);

            var expected = Path.Combine(userData, "roots", FuseStorePaths.RootHash(nested), FuseStorePaths.DatabaseFileName);
            Assert.Equal(Path.GetFullPath(expected), FuseStorePaths.ResolveDatabasePath(nested));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveDatabasePath_TwoDistinctNonGitRoots_ResolveToDistinctStores()
    {
        // R34: two unrelated non-git directories must not collide on one store (the dogfood pollution case).
        var root = CreateTempDirectory();
        var userData = Path.Combine(root, "user-data");
        Directory.CreateDirectory(userData);
        Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, userData);

        try
        {
            var alpha = Path.Combine(root, "alpha");
            var beta = Path.Combine(root, "beta");
            Directory.CreateDirectory(alpha);
            Directory.CreateDirectory(beta);

            var alphaStore = FuseStorePaths.ResolveDatabasePath(alpha);
            var betaStore = FuseStorePaths.ResolveDatabasePath(beta);

            Assert.NotEqual(alphaStore, betaStore);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(userData, "roots")), alphaStore, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(Path.Combine(userData, "roots")), betaStore, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-store-paths-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
