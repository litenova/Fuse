using Fuse.Cli.Commands;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;

namespace Fuse.Cli.Tests.Commands;

/// <summary>
///     Verifies the terminal progress contract for known and unknown index-stage totals.
/// </summary>
public sealed class IndexProgressRendererTests
{
    [Fact]
    public void Unknown_total_renders_a_spinner_without_a_percentage()
    {
        var console = new CapturingConsoleUI();
        var renderer = new IndexProgressRenderer(console, json: false);

        renderer.Render(CreateSnapshot(totalUnits: null, phasePercent: null), force: true);

        var output = Assert.Single(console.Steps);
        Assert.Contains("Phase 1/3 [|] discovering source files", output);
        Assert.DoesNotContain('%', output);
    }

    [Fact]
    public void Known_total_renders_the_ascii_bar_and_percentage()
    {
        var console = new CapturingConsoleUI();
        var renderer = new IndexProgressRenderer(console, json: false);

        renderer.Render(CreateSnapshot(totalUnits: 10, phasePercent: 40), force: true);

        Assert.Contains("Phase 1/3 [####------] 40% 4/10 files", Assert.Single(console.Steps));
    }

    private static IndexJobSnapshot CreateSnapshot(long? totalUnits, double? phasePercent) =>
        new(
            JobId: "job-1",
            Root: "C:/repo",
            State: IndexJobState.Running,
            Phase: IndexPhase.Inventory,
            PhaseNumber: 1,
            PhaseCount: 3,
            CompletedUnits: 4,
            TotalUnits: totalUnits,
            PhasePercent: phasePercent,
            EstimatedRemaining: null,
            CurrentItem: "discovering source files",
            StartedAt: DateTimeOffset.UtcNow,
            Elapsed: TimeSpan.FromSeconds(2),
            Counts: IndexCountSnapshot.Empty,
            Storage: IndexStorageSnapshot.Empty,
            Warnings: [],
            ErrorCode: null,
            ErrorMessage: null);

    private sealed class CapturingConsoleUI : IConsoleUI
    {
        public List<string> Steps { get; } = [];

        public void WriteSuccess(string message)
        {
        }

        public void WriteError(string message)
        {
        }

        public void WriteStep(string message) => Steps.Add(message);

        public void WriteResult(string message)
        {
        }
    }
}
