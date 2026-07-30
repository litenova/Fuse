using System.Text;
using Fuse.Cli.Rpc;
using Fuse.Semantics;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Implements <c>fuse_refactor</c>: compiler-executed, verify-gated refactors staged as a diff. Nothing is
///     written to the working tree.
/// </summary>
/// <remarks>
///     Each operation recompiles and returns the diff only when it introduces no new diagnostic; otherwise it
///     abstains naming the offending sites, so a caller never receives a mostly-right diff.
/// </remarks>
internal static class RefactorToolOperations
{
    private static readonly TimeSpan HostRoutingTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs one refactor operation, mapping an unexpected failure to an operational error.</summary>
    /// <param name="path">The workspace directory.</param>
    /// <param name="symbol">The symbol to rename, or the method name for a signature operation.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="operation">The requested compiler refactor operation.</param>
    /// <param name="containingType">The declaring type used to disambiguate a signature operation.</param>
    /// <param name="parameterType">The new parameter type for an add-parameter operation.</param>
    /// <param name="parameterName">The parameter name for a signature operation.</param>
    /// <param name="argument">The call-site argument added by an add-parameter operation.</param>
    /// <param name="newOrder">The comma-separated parameter order for a reorder operation.</param>
    /// <param name="diagnosticId">The diagnostic identifier for an apply-codefix operation.</param>
    /// <param name="file">The repository-relative file for an apply-codefix operation.</param>
    /// <param name="cancellationToken">A token to cancel the refactor.</param>
    /// <param name="runtime">The host-owned compiler cache used for this refactor.</param>
    /// <returns>The staged per-file diffs, or an explicit abstention.</returns>
    internal static Task<string> ExecuteAsync(
        string path = ".",
        string symbol = "",
        string newName = "",
        string operation = "rename",
        string containingType = "",
        string parameterType = "",
        string parameterName = "",
        string argument = "default",
        string newOrder = "",
        string diagnosticId = "",
        string file = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => RefactorCoreAsync(
            path, symbol, newName, operation, containingType, parameterType, parameterName, argument, newOrder,
            diagnosticId, file, cancellationToken, runtime: runtime));

    /// <summary>
    ///     Executes a refactor, optionally routing it to the shared daemon so a repeated request reuses the
    ///     host-owned warm solution instead of re-opening MSBuild.
    /// </summary>
    /// <param name="path">The workspace directory.</param>
    /// <param name="symbol">The symbol to rename, or the method name for a signature operation.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="operation">The requested compiler refactor operation.</param>
    /// <param name="containingType">The declaring type used to disambiguate a signature operation.</param>
    /// <param name="parameterType">The new parameter type for an add-parameter operation.</param>
    /// <param name="parameterName">The parameter name for a signature operation.</param>
    /// <param name="argument">The call-site argument added by an add-parameter operation.</param>
    /// <param name="newOrder">The comma-separated parameter order for a reorder operation.</param>
    /// <param name="diagnosticId">The diagnostic identifier for an apply-codefix operation.</param>
    /// <param name="file">The repository-relative file for an apply-codefix operation.</param>
    /// <param name="cancellationToken">A token to cancel the refactor.</param>
    /// <param name="routeToHost">Whether to offer the request to the shared daemon first.</param>
    /// <param name="runtime">The host-owned compiler cache used for this refactor.</param>
    /// <returns>The staged per-file diffs, or an explicit abstention.</returns>
    internal static async Task<string> RefactorCoreAsync(
        string path,
        string symbol,
        string newName,
        string operation,
        string containingType,
        string parameterType,
        string parameterName,
        string argument,
        string newOrder,
        string diagnosticId,
        string file,
        CancellationToken cancellationToken,
        bool routeToHost = true,
        FuseMcpRuntime? runtime = null)
    {
        var root = WorkspacePathResolver.ResolveRepositoryRoot(path);
        if (routeToHost && Commands.McpServeCommand.IsDaemonEnabled())
        {
            var request = new RefactorRequestDto(
                symbol, newName, operation, containingType, parameterType, parameterName,
                argument, newOrder, diagnosticId, file);
            var remote = await FuseHostClient.TryRefactorAsync(root, request, HostRoutingTimeout, cancellationToken);
            if (remote is not null)
                return remote.Output;
        }

        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return "cannot refactor: no solution or project found. fuse_refactor abstains.";

        var containing = string.IsNullOrWhiteSpace(containingType) ? null : containingType;
        var warmSolutions = runtime?.WarmSolutions;
        switch (operation.Trim().ToLowerInvariant())
        {
            case "add-parameter":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(parameterType) || string.IsNullOrWhiteSpace(parameterName))
                    return "Error: add-parameter needs the method (symbol), parameterType, and parameterName.";
                return RenderChangeSignature(
                    await new ChangeSignatureRefactorer(warmSolutions).AddParameterAsync(
                        target, symbol, containing, parameterType, parameterName, argument, cancellationToken),
                    $"add parameter '{parameterType} {parameterName}' to {symbol}");

            case "add-cancellation-token":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: add-cancellation-token needs the method (symbol).";
                var tokenName = string.IsNullOrWhiteSpace(parameterName) ? "cancellationToken" : parameterName;
                return RenderChangeSignature(
                    await new ChangeSignatureRefactorer(warmSolutions).ThreadCancellationTokenAsync(
                        target, symbol, containing, tokenName, cancellationToken),
                    $"thread a CancellationToken '{tokenName}' through {symbol}");

            case "remove-parameter":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(parameterName))
                    return "Error: remove-parameter needs the method (symbol) and parameterName.";
                return RenderChangeSignature(
                    await new ChangeSignatureRefactorer(warmSolutions).RemoveParameterAsync(
                        target, symbol, containing, parameterName, cancellationToken),
                    $"remove parameter '{parameterName}' from {symbol}");

            case "reorder-parameters":
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newOrder))
                    return "Error: reorder-parameters needs the method (symbol) and newOrder (comma-separated parameter names).";
                var order = newOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return RenderChangeSignature(
                    await new ChangeSignatureRefactorer(warmSolutions).ReorderParametersAsync(
                        target, symbol, containing, order, cancellationToken),
                    $"reorder parameters of {symbol}");

            case "extract-interface":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: extract-interface needs the class (symbol).";
                return RenderTypeRefactor(
                    await new TypeRefactorer(warmSolutions).ExtractInterfaceAsync(
                        target, symbol, string.IsNullOrWhiteSpace(newName) ? null : newName, cancellationToken),
                    $"extract interface from {symbol}");

            case "move-type":
                if (string.IsNullOrWhiteSpace(symbol))
                    return "Error: move-type needs the type (symbol).";
                return RenderTypeRefactor(
                    await new TypeRefactorer(warmSolutions).MoveTypeToOwnFileAsync(target, symbol, cancellationToken),
                    $"move {symbol} to its own file");

            case "apply-codefix":
                return await ApplyCodeFixAsync(root, target, diagnosticId, file, warmSolutions, cancellationToken);

            case "rename":
            case "":
                return await RenameAsync(target, symbol, newName, warmSolutions, cancellationToken);

            default:
                return $"Error: unknown operation '{operation}'. Use rename, add-parameter, add-cancellation-token, "
                    + "remove-parameter, reorder-parameters, extract-interface, move-type, or apply-codefix.";
        }
    }

    private static async Task<string> RenameAsync(
        string target,
        string symbol,
        string newName,
        WarmSolutionCache? warmSolutions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newName))
            return "Error: provide the symbol to rename and the new name.";

        var result = await new RenameRefactorer(warmSolutions).RenameAsync(target, symbol, newName, cancellationToken);
        if (!result.Renamed)
            return $"cannot rename: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged rename: {result.OldName} -> {result.NewName} ({result.Diffs.Count} file(s) changed, not written to disk)");
        foreach (var diff in result.Diffs)
        {
            builder.AppendLine($"--- {diff.FilePath}");
            builder.AppendLine(diff.UnifiedDiff);
        }

        builder.AppendLine();
        builder.AppendLine(
            "Review this diff and re-check with fuse_check before applying; a rename crossing a boundary Roslyn does "
            + "not see (a string, reflection) would surface as a diagnostic there.");
        return builder.ToString().TrimEnd();
    }

    private static async Task<string> ApplyCodeFixAsync(
        string root,
        string target,
        string diagnosticId,
        string file,
        WarmSolutionCache? warmSolutions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diagnosticId) || string.IsNullOrWhiteSpace(file))
            return "Error: apply-codefix needs a diagnosticId and a file.";

        var (resolved, _, error) = WorkspacePathResolver.ResolveWorkspacePath(root, file, "refactor");
        if (!resolved)
            return error!;

        var result = await new CodeFixApplier(warmSolutions).ApplyCodeFixAsync(target, diagnosticId, file, cancellationToken);
        if (!result.Changed)
            return $"cannot apply the fix for {diagnosticId} in {file}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine(
            $"staged apply-codefix {result.DiagnosticId} in {result.FilePath} ({result.Applied} fix(es) applied, "
            + "verified clean, not written to disk)");
        builder.AppendLine($"--- {result.FilePath} (full new content)");
        builder.AppendLine(result.NewText);
        builder.AppendLine();
        builder.AppendLine(
            "This diff verified clean (the target diagnostic reached zero with no new compile error). Review it and "
            + "re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }

    // Renders a type-refactor outcome (extract-interface, move-type): the staged full-file content, or the abstention.
    private static string RenderTypeRefactor(TypeRefactorResult result, string what)
    {
        if (!result.Changed)
            return $"cannot {what}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged {result.Summary} (verified clean; {result.Diffs.Count} file(s), not written to disk)");
        foreach (var diff in result.Diffs)
        {
            builder.AppendLine($"--- {diff.FilePath} (full new content)");
            builder.AppendLine(diff.NewText);
        }

        builder.AppendLine();
        builder.AppendLine("This diff verified clean (no new compile diagnostic). Review it and re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }

    // Renders a change-signature outcome: the staged diff plus any manual follow-up sites, or the abstention.
    private static string RenderChangeSignature(ChangeSignatureResult result, string what)
    {
        if (!result.Changed)
            return $"cannot {what}: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged {what}: {result.OldSignature} (added {result.Added}; {result.Diffs.Count} file(s) changed, not written to disk)");
        foreach (var diff in result.Diffs)
        {
            builder.AppendLine($"--- {diff.FilePath}");
            builder.AppendLine(diff.UnifiedDiff);
        }

        if (result.ManualFollowUps.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(
                $"Manual follow-ups ({result.ManualFollowUps.Count} call site(s) had no in-scope value, so 'default' was "
                + "passed; thread a real value):");
            foreach (var site in result.ManualFollowUps)
                builder.AppendLine($"  {site}");
        }

        builder.AppendLine();
        builder.AppendLine("This diff verified clean (no new compile diagnostic). Review it and re-check with fuse_check before applying.");
        return builder.ToString().TrimEnd();
    }
}
