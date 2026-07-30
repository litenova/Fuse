using Fuse.Indexing;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

/// <summary>
///     Guards retrieval services against regressing to the all-purpose index store when a focused port is enough.
/// </summary>
public sealed class FocusedStoreDependencyTests
{
    [Theory]
    [InlineData(typeof(GraphCentralityPrior), typeof(IWorkspaceIndexGraphStore))]
    [InlineData(typeof(GraphExpansionEngine), typeof(IWorkspaceIndexGraphStore))]
    [InlineData(typeof(LexicalCandidateGenerator), typeof(IWorkspaceIndexQueryStore))]
    [InlineData(typeof(ExactCandidateGenerator), typeof(IWorkspaceIndexGraphStore))]
    [InlineData(typeof(PathCandidateGenerator), typeof(IWorkspaceIndexQueryStore))]
    [InlineData(typeof(SemanticResolver), typeof(IWorkspaceIndexGraphStore))]
    [InlineData(typeof(RepairPacketBuilder), typeof(IWorkspaceIndexQueryStore))]
    [InlineData(typeof(NavigationMapBuilder), typeof(IWorkspaceIndexQueryStore))]
    public void Retrieval_service_uses_its_focused_store_port(Type serviceType, Type expectedStorePort)
    {
        var constructor = Assert.Single(serviceType.GetConstructors());

        Assert.Equal(expectedStorePort, constructor.GetParameters()[0].ParameterType);
        Assert.NotEqual(typeof(IWorkspaceIndexStore), constructor.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Session_claim_ledger_uses_the_verification_session_port()
    {
        var storeParameter = typeof(SessionClaimLedger)
            .GetMethod(nameof(SessionClaimLedger.SaveAsync))!
            .GetParameters()[0];

        Assert.Equal(typeof(IWorkspaceIndexVerificationSessionStore), storeParameter.ParameterType);
    }
}
