using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Rpc;

// Focused compiler and verification operations behind the JSON-RPC adapter.
internal sealed class FuseHostVerificationOperations
{
    private readonly FuseHostService _host;
    private CompilerStateBudget? _compilerStateBudget;

    internal FuseHostVerificationOperations(FuseHostService host)
    {
        _host = host;
    }

    internal async Task<CheckDeltaDto> CheckDeltaAsync(string root, string session)
    {
        var current = _host.ResidentWorkspaces.TryGetCurrentDiagnostics(root, _host.LifetimeToken);
        if (current is null)
            return new CheckDeltaDto(false, [], []);

        await using var store = await FuseHostIndexOperations.OpenStoreAsync(root, _host.LifetimeToken);
        var baseline = await store.GetCheckSessionBaselineAsync(session, _host.LifetimeToken);
        if (baseline is null)
        {
            await store.SaveCheckSessionBaselineAsync(session, root, current, _host.LifetimeToken);
            return new CheckDeltaDto(true, [], []);
        }

        var delta = DiagnosticDelta.Compute(baseline.Diagnostics, current);
        return new CheckDeltaDto(
            true,
            delta.Introduced.Select(ToCheckDiagnosticDto).ToList(),
            delta.Resolved.Select(ToCheckDiagnosticDto).ToList());
    }

    internal async Task<CheckOverlayResultDto> CheckOverlayAsync(
        string root,
        string relativeFilePath,
        string newContent,
        bool includeAnalyzers)
    {
        var diagnostics = await _host.ResidentWorkspaces.TryCheckOverlayAsync(
            root,
            relativeFilePath,
            newContent,
            includeAnalyzers,
            _host.LifetimeToken);
        return diagnostics is null
            ? new CheckOverlayResultDto(false, [])
            : new CheckOverlayResultDto(true, diagnostics.Select(ToCheckDiagnosticDto).ToList());
    }

    internal async Task<DoctorResultDto> DoctorAsync(string root)
    {
        BudgetFor(root).ActivateWarm();
        var report = await Fuse.Cli.Commands.DoctorCommand.BuildLiveReportAsync(
            _host.Indexer,
            root,
            _host.LifetimeToken);
        return new DoctorResultDto(report);
    }

    internal async Task<RefactorResultDto> RefactorAsync(string root, RefactorRequestDto request)
    {
        BudgetFor(root).ActivateWarm();
        var output = await RefactorToolOperations.RefactorCoreAsync(
            root,
            request.Symbol,
            request.NewName,
            request.Operation,
            request.ContainingType,
            request.ParameterType,
            request.ParameterName,
            request.Argument,
            request.NewOrder,
            request.DiagnosticId,
            request.File,
            _host.LifetimeToken,
            routeToHost: false,
            runtime: _host.Runtime);
        return new RefactorResultDto(output);
    }

    internal async Task<CaptureCheckResultDto> CheckCaptureAsync(
        string root,
        string relativeFilePath,
        string newContent)
    {
        BudgetFor(root).ActivateCapture();
        var result = await CheckToolOperations.TryOracleFromCaptureBundleAsync(
            _host.Runtime,
            root,
            relativeFilePath,
            newContent,
            new BuildCaptureClient(processRunner: _host.Runtime.ProcessRunner),
            _host.LifetimeToken);
        return result is { Verified: true }
            ? new CaptureCheckResultDto(true, null, result.Diagnostics.Select(ToCheckDiagnosticDto).ToList())
            : new CaptureCheckResultDto(false, result?.Reason, []);
    }

    internal void ActivateResidentBudget(string root)
    {
        BudgetFor(root).ActivateResident();
    }

    private CompilerStateBudget BudgetFor(string root) =>
        _compilerStateBudget ??= new CompilerStateBudget(
            root,
            warmSolutions: _host.Runtime.WarmSolutions,
            pooledWorkers: _host.Runtime.PooledCheckWorkers,
            residentWorkspaces: _host.ResidentWorkspaces);

    private static CheckDiagnosticDto ToCheckDiagnosticDto(CheckDiagnostic diagnostic) =>
        new(diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.FilePath, diagnostic.Line);
}
