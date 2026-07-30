using Fuse.Cli.Rpc;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Xunit;

namespace Fuse.Cli.Tests;

public sealed class CompilerStateBudgetTests
{
    private sealed class TrackingWorkspace : Microsoft.CodeAnalysis.Workspace
    {
        public TrackingWorkspace() : base(MefHostServices.DefaultHost, "Tracking") { }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool finalize)
        {
            Disposed = true;
            base.Dispose(finalize);
        }
    }

    private sealed class FakeChannel : ICheckWorkerChannel
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> RequestAsync(string requestLine, CancellationToken cancellationToken) =>
            Task.FromResult("{\"verified\":true,\"diagnostics\":[]}");

        public void Dispose() { }
    }

    [Fact]
    public async Task Default_cap_evicts_the_previous_root_owned_compiler_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-compiler-budget", Guid.NewGuid().ToString("N"));
        var workspace = new TrackingWorkspace();
        var warm = new WarmSolutionCache(
            loader: (_, _) => Task.FromResult(new LoadedWorkspace(workspace, workspace.CurrentSolution, [])),
            signature: _ => "v1");
        var pool = new PooledCheckWorker(channelFactory: _ => new FakeChannel());
        try
        {
            await warm.OpenAsync(Path.Combine(root, "App.sln"), CancellationToken.None);
            await pool.TryCheckAsync("/logs/App.complog", "A.cs", "class A {}", CancellationToken.None, root);
            Assert.Equal(1, warm.HeldCount);
            Assert.Equal(1, pool.HeldCount);

            var budget = new CompilerStateBudget(root, cap: 1, warmSolutions: warm, pooledWorkers: pool);
            budget.ActivateWarm();
            budget.ActivateCapture();
            Assert.Equal(0, warm.HeldCount);
            Assert.True(workspace.Disposed);

            budget.ActivateWarm();
            Assert.Equal(0, pool.HeldCount);
        }
        finally
        {
            warm.Dispose();
            pool.Dispose();
        }
    }
}
