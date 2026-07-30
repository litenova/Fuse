using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// U1 / Decision D2: fuse_workspace action=apply is the server's one explicit tree-write path. It must be a dry run
// unless write=true, must actually write when asked, and must refuse a path that escapes the workspace root. The
// Apply does not use the indexer, but every loop tool still requires a repository identity.
public sealed class WorkspaceApplyTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-apply-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    [Fact]
    public async Task Dry_run_reports_without_writing()
    {
        var root = TempRoot();
        try
        {
            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "src/A.cs", content: "class A {}");
            Assert.Contains("dry run", result);
            Assert.Contains("would create", result);
            Assert.False(File.Exists(Path.Combine(root, "src", "A.cs")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Write_creates_the_file_with_the_content()
    {
        var root = TempRoot();
        try
        {
            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "src/A.cs", content: "class A {}", write: true);
            Assert.Contains("applied", result);
            var written = Path.Combine(root, "src", "A.cs");
            Assert.True(File.Exists(written));
            Assert.Equal("class A {}", await File.ReadAllTextAsync(written));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_path_escaping_the_root_is_refused()
    {
        var root = TempRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "escaped.txt");
        try
        {
            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "../escaped.txt", content: "x", write: true);
            Assert.Contains("refusing to write", result);
            Assert.Contains("outside the workspace root", result);
            Assert.False(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public async Task Apply_without_a_file_errors()
    {
        var root = TempRoot();
        try
        {
            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "", content: "x", write: true);
            // R15 operational-error taxonomy: a missing file argument is a validation_error, not a bare "Error".
            Assert.StartsWith("validation_error:", result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Write_matches_expectedHash_appliesAtomically()
    {
        var root = TempRoot();
        try
        {
            var target = Path.Combine(root, "src", "A.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "class A {}");
            var hash = Sha256Hex("class A {}");

            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "src/A.cs", content: "class A { int x; }", write: true, expectedHash: hash);

            Assert.Contains("applied", result);
            Assert.Contains("atomic", result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("class A { int x; }", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Write_conflict_whenFileChangedSinceDerived_isRefused_notClobbered()
    {
        var root = TempRoot();
        try
        {
            var target = Path.Combine(root, "src", "A.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "class A { /* changed on disk */ }");
            var staleHash = Sha256Hex("class A {}"); // the content the edit was derived from, now out of date.

            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "src/A.cs", content: "class A { int x; }", write: true, expectedHash: staleHash);

            Assert.StartsWith("validation_error:", result);
            Assert.Contains("conflict", result, StringComparison.OrdinalIgnoreCase);
            // The on-disk content is not clobbered.
            Assert.Equal("class A { /* changed on disk */ }", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Write_expectedHash_forMissingFile_isRefusedAsConflict()
    {
        var root = TempRoot();
        try
        {
            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "src/New.cs", content: "class New {}", write: true, expectedHash: Sha256Hex("something"));
            Assert.StartsWith("validation_error:", result);
            Assert.Contains("conflict", result, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "src", "New.cs")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DryRun_withConflict_reportsConflict_beforeWriting()
    {
        var root = TempRoot();
        try
        {
            var target = Path.Combine(root, "src", "A.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "class A { changed }");

            // Even a dry run reports the conflict (the check runs before the dry-run report), so the agent learns
            // to re-read before it flips write=true.
            var result = await WorkspaceToolOperations.ExecuteAsync(null!, null!, "apply", root, file: "src/A.cs", content: "class A {}", write: false, expectedHash: Sha256Hex("class A {}"));
            Assert.Contains("conflict", result, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
}
