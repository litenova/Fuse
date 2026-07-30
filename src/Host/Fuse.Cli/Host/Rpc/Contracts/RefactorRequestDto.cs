namespace Fuse.Cli.Rpc;

/// <summary>The input for a staged refactor executed by the root's compiler-state owner.</summary>
/// <param name="Symbol">The symbol to rename, or the method name for a signature operation.</param>
/// <param name="NewName">The new name.</param>
/// <param name="Operation">The requested compiler refactor operation.</param>
/// <param name="ContainingType">The declaring type used to disambiguate a signature operation.</param>
/// <param name="ParameterType">The new parameter type for an add-parameter operation.</param>
/// <param name="ParameterName">The parameter name for a signature operation.</param>
/// <param name="Argument">The call-site argument added by an add-parameter operation.</param>
/// <param name="NewOrder">The comma-separated parameter order for a reorder operation.</param>
/// <param name="DiagnosticId">The diagnostic identifier for an apply-codefix operation.</param>
/// <param name="File">The repository-relative file for an apply-codefix operation.</param>
public sealed record RefactorRequestDto(
    string Symbol,
    string NewName,
    string Operation,
    string ContainingType,
    string ParameterType,
    string ParameterName,
    string Argument,
    string NewOrder,
    string DiagnosticId,
    string File);

/// <summary>The rendered staged refactor result produced by the root's compiler-state owner.</summary>
/// <param name="Output">The unchanged staged diff or abstention text.</param>
public sealed record RefactorResultDto(string Output);

/// <summary>The rendered live semantic-load diagnosis produced by the root's compiler-state owner.</summary>
/// <param name="Report">The unchanged doctor report text.</param>
public sealed record DoctorResultDto(string Report);
