using Microsoft.CodeAnalysis;

namespace Fuse.Semantics;

/// <summary>
///     Reconciles a project load tier with the real toolchain: when the in-process design-time compilation of a
///     project carries error-severity diagnostics, the tier decision is verified against a scoped real
///     <c>dotnet build</c>. A project whose in-process compilation is incomplete (for example a Razor/Blazor
///     project whose source generator did not load in-process, so its generated partial classes are missing and
///     the compiler reports phantom errors the real build never produces) still builds clean with the SDK's
///     correct generator versions, and that clean build is the honest basis for its tier.
/// </summary>
/// <remarks>
///     <para>
///         The in-process design-time load runs the build with the Roslyn the process bundles. A source
///         generator built against a newer Roslyn (for example the .NET 10 SDK's Razor
///         generator) fails to load in-process, so the partial classes it would generate are missing and the
///         compiler reports phantom errors (for example <c>CS0115</c> on a Blazor code-behind) that the real
///         build does not have. The tier decision is ground truth, not an approximation: when the in-process
///         compilation of a project reports error-severity diagnostics, this reconciler runs a scoped real
///         <c>dotnet build</c> of that project and, when the build succeeds with zero errors, promotes the
///         project's load report to a clean load so the tier reflects the toolchain rather than the in-process
///         approximation.
///     </para>
///     <para>
///         Reconciliation is conservative and never over-promotes: a project is promoted only when the real build
///         runs and reports zero errors. When the build fails, times out, or the toolchain cannot run, the
///         project keeps its in-process reason (graph-grade), so a genuinely broken project is never hidden.
///         Warnings do not affect the tier (the decision is error-only, matching the in-process rule).
///     </para>
/// </remarks>
internal sealed class BuildTierReconciler
{
    private readonly IProcessRunner _processRunner;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BuildTierReconciler" /> class.
    /// </summary>
    /// <param name="processRunner">The host-owned runner that runs the scoped builds.</param>
    public BuildTierReconciler(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    /// <summary>
    ///     Reconciles the per-project load reports of a snapshot against the real toolchain, returning the
    ///     (possibly promoted) reports. Projects that loaded clean are returned unchanged; a project that loaded
    ///     with compile errors is verified with a scoped real build, and is promoted to a clean load when that
    ///     build succeeds with zero errors.
    /// </summary>
    /// <param name="rootDirectory">The repository root the project paths are relative to.</param>
    /// <param name="snapshot">The in-process load snapshot whose per-project reports to reconcile.</param>
    /// <param name="cancellationToken">A token that stops the scoped builds.</param>
    /// <returns>The reconciled per-project reports, plus the load diagnostics that record any promotion.</returns>
    public async Task<(IReadOnlyList<ProjectLoadReport> Reports, IReadOnlyList<DiagnosticRecord> Diagnostics)>
        ReconcileAsync(string rootDirectory, RoslynWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var reports = new List<ProjectLoadReport>(snapshot.ProjectReports.Count);
        var diagnostics = new List<DiagnosticRecord>();
        foreach (var report in snapshot.ProjectReports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!report.Loaded || report.Reason != RoslynWorkspaceLoader.LoadsWithErrorsReason)
            {
                reports.Add(report);
                continue;
            }

            var errorCount = await new BuildGradeChecker(processRunner: _processRunner)
                .CountProjectErrorsAsync(rootDirectory, report.FilePath, cancellationToken);
            if (errorCount is 0)
            {
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Info,
                    "tier-reconciled",
                    $"Project '{report.Name}' loaded with in-process compile errors but the scoped " +
                    $"dotnet build reported 0 errors; promoted to a clean load (oracle-grade).",
                    report.FilePath));
                reports.Add(new ProjectLoadReport(report.Name, report.FilePath, Loaded: true, RoslynWorkspaceLoader.CleanLoadReason));
            }
            else
            {
                // The real build failed, has errors, or could not run: keep the in-process graph-grade reason.
                reports.Add(report);
            }
        }

        return (reports, diagnostics);
    }
}
