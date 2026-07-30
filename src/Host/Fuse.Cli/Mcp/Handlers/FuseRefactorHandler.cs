using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The <c>fuse_refactor</c> MCP tool: compiler-executed, verify-gated refactors staged as a diff.
/// </summary>
[McpServerToolType]
internal sealed class FuseRefactorHandler
{
    /// <summary>Stages one compiler-executed refactor as a verified diff.</summary>
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
    [McpServerTool(Name = "fuse_refactor", ReadOnly = true)]
    [Description("Compiler-executed, verify-gated refactors returned as a staged diff (nothing is written to disk). operation=rename (default): rename a symbol and all its references through Roslyn (a same-named unrelated symbol is not touched). operation=add-parameter: add a trailing parameter to a method and its override/interface family, threading an explicit argument (the `argument` value) into every call site. operation=add-cancellation-token: add a CancellationToken parameter and thread an in-scope token into every call site that has one, listing token-less sites as manual follow-ups. operation=remove-parameter: remove a parameter (named by parameterName) and drop its argument at every call site, abstaining when the parameter is used in a body or a call site passes a non-trivial (possibly side-effecting) argument. operation=reorder-parameters: reorder parameters into `newOrder` (comma-separated names), abstaining if any call site uses positional arguments (only named-argument call sites are safe to reorder). operation=extract-interface: generate an interface from a class's public instance methods and properties (name it with newName, else I<Class>) and make the class implement it. operation=move-type: move a top-level type (symbol) to its own new file named after it, removing it from its current file. operation=apply-codefix: apply the repo's own analyzer code fix for `diagnosticId` in `file`, driving that diagnostic to zero (discovers the analyzers and [ExportCodeFixProvider] fixes from the project's analyzer references). The signature and type operations recompile the solution and return the diff ONLY when no new diagnostic is introduced; otherwise they abstain naming the offending sites (never a mostly-right diff). Rename and the signature ops answer only when the whole solution loads cleanly; abstain otherwise. Review and apply the staged diff with normal editing tools, then run the repository's required gates.")]
    public static Task<string> ExecuteAsync(
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The simple name of the symbol to rename, or the method name for a signature operation.")] string symbol = "",
        [Description("The new name (rename only).")] string newName = "",
        [Description("The operation: rename (default), add-parameter, add-cancellation-token, remove-parameter, reorder-parameters, extract-interface, move-type, or apply-codefix.")] string operation = "rename",
        [Description("The declaring type's simple name, to disambiguate a method shared across types (signature operations).")] string containingType = "",
        [Description("The new parameter's type, as written in source (add-parameter).")] string parameterType = "",
        [Description("The new parameter's name (add-parameter; defaults to cancellationToken for add-cancellation-token).")] string parameterName = "",
        [Description("The argument expression added at every call site (add-parameter; defaults to 'default').")] string argument = "default",
        [Description("The parameter names in the desired order, comma-separated (reorder-parameters).")] string newOrder = "",
        [Description("The diagnostic id to fix (apply-codefix), for example IDE0090 or a repo analyzer id.")] string diagnosticId = "",
        [Description("The repo-relative file to apply the code fix in (apply-codefix).")] string file = "",
        CancellationToken cancellationToken = default,
        FuseMcpRuntime? runtime = null) =>
        RefactorToolOperations.ExecuteAsync(
            path, symbol, newName, operation, containingType, parameterType, parameterName, argument, newOrder,
            diagnosticId, file, cancellationToken, runtime);
}
