using System.Text.Json;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Cli.Serialization;

namespace Fuse.Cli.Tests;

// Pins the host RPC wire contract: the DTOs must serialize through the source-generated FuseHostJsonContext with
// camelCase property names, which is exactly what protocol.ts in the extension mirrors. A change to a DTO shape
// here fails this test, signalling that protocol.ts (and the extension contract test) must change in lockstep.
public sealed class FuseHostContractTests
{
    [Fact]
    public void Handshake_SerializesCamelCaseThroughSourceGenContext()
    {
        var json = JsonSerializer.Serialize(
            new FuseHostHandshake("3.0.0", FuseHostService.ProtocolVersion, "abc123token"),
            FuseHostJsonContext.Default.FuseHostHandshake);

        Assert.Contains("\"hostVersion\":\"3.0.0\"", json);
        Assert.Contains("\"protocolVersion\":", json);
        Assert.Contains("\"sessionToken\":\"abc123token\"", json);
    }

    [Fact]
    public void IndexResult_SerializesRichSummaryCamelCase()
    {
        var index = new IndexResultDto(
            "Warm", 128, 940, "semantic", 512, 7, 14, true, "3.2.0",
            [new LanguageCountDto("csharp", 120), new LanguageCountDto("python", 8)]);

        var json = JsonSerializer.Serialize(index, FuseHostJsonContext.Default.IndexResultDto);

        Assert.Contains("\"indexState\":\"Warm\"", json);
        Assert.Contains("\"mode\":\"semantic\"", json);
        Assert.Contains("\"symbolCount\":512", json);
        Assert.Contains("\"routeCount\":7", json);
        Assert.Contains("\"schemaVersion\":14", json);
        Assert.Contains("\"fullTextSearch\":true", json);
        Assert.Contains("\"fuseVersion\":\"3.2.0\"", json);
        Assert.Contains("\"languages\":[{\"language\":\"csharp\",\"count\":120}", json);
    }

    [Fact]
    public void IndexJobSnapshot_SerializesNamedLifecycleEnumsAcrossPublicContexts()
    {
        var snapshot = new IndexJobSnapshot(
            "job-1",
            "C:/repo",
            IndexJobState.Running,
            IndexPhase.SyntaxExtraction,
            2,
            4,
            4,
            10,
            40,
            null,
            "src/Widget.cs",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            IndexCountSnapshot.Empty,
            IndexStorageSnapshot.Empty,
            [],
            null,
            null);

        var cli = JsonSerializer.Serialize(snapshot, IndexCliJsonContext.Default.IndexJobSnapshot);
        var host = JsonSerializer.Serialize(snapshot, FuseHostJsonContext.Default.IndexJobSnapshot);

        Assert.Contains("\"state\":\"Running\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"SyntaxExtraction\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Running\"", host, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"SyntaxExtraction\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Stats_RoundTripsThroughSourceGenContext()
    {
        var original = new FuseHostStats("3.0.0", 4242, 1500, 123_456_789);

        var json = JsonSerializer.Serialize(original, FuseHostJsonContext.Default.FuseHostStats);
        var back = JsonSerializer.Deserialize(json, FuseHostJsonContext.Default.FuseHostStats);

        Assert.Contains("\"workingSetBytes\":123456789", json);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Graph_SerializesNodesAndEdgesCamelCase()
    {
        var graph = new GraphDto(
            [new GraphNodeDto("a/B.cs", ["B"], 0.5, 1200, "Seed")],
            [new GraphEdgeDto("a/B.cs", "a/C.cs", 1.0, "reference")],
            "Files");

        var json = JsonSerializer.Serialize(graph, FuseHostJsonContext.Default.GraphDto);

        Assert.Contains("\"declaredTypes\":[\"B\"]", json);
        Assert.Contains("\"tokenCost\":1200", json);
        Assert.Contains("\"kind\":\"reference\"", json);
        Assert.Contains("\"detail\":\"Files\"", json);
    }

    [Fact]
    public void Node_OmitsNullRole()
    {
        // The optional role is written only when a scope is active, so an unscoped graph node carries no role key.
        var json = JsonSerializer.Serialize(
            new GraphNodeDto("a.cs", [], 0.0, 0, null), FuseHostJsonContext.Default.GraphNodeDto);

        Assert.DoesNotContain("role", json);
    }

    [Fact]
    public void ScopeResult_SerializesFilesAndTotalsCamelCase()
    {
        var scope = new ScopeResultDto(
            "search", [new ScopeFileDto("a/B.cs", 480)], 480, "/tmp/x.fuse.xml");

        var json = JsonSerializer.Serialize(scope, FuseHostJsonContext.Default.ScopeResultDto);

        Assert.Contains("\"mode\":\"search\"", json);
        Assert.Contains("\"tokenCost\":480", json);
        Assert.Contains("\"totalTokens\":480", json);
        Assert.Contains("\"payloadPath\":\"/tmp/x.fuse.xml\"", json);
    }

    [Fact]
    public void ScopeResult_OmitsNullPayloadPath()
    {
        var json = JsonSerializer.Serialize(
            new ScopeResultDto("search", [], 0, null), FuseHostJsonContext.Default.ScopeResultDto);

        Assert.DoesNotContain("payloadPath", json);
    }

    [Fact]
    public void Diagnostics_SerializesSecretsHotspotsAndGapsCamelCase()
    {
        var diagnostics = new DiagnosticsDto(
            [new SecretDiagnosticDto("a/Config.cs", "github-token", 12, 4, 12, 44)],
            [new HotspotDiagnosticDto("a/Big.cs", 4800)],
            ["a/Orphan.cs"],
            ["a/Generated.g.cs"]);

        var json = JsonSerializer.Serialize(diagnostics, FuseHostJsonContext.Default.DiagnosticsDto);

        Assert.Contains("\"kind\":\"github-token\"", json);
        Assert.Contains("\"startLine\":12", json);
        Assert.Contains("\"endColumn\":44", json);
        Assert.Contains("\"hotspots\":[{\"path\":\"a/Big.cs\",\"tokenCost\":4800}]", json);
        Assert.Contains("\"graphGaps\":[\"a/Orphan.cs\"]", json);
        Assert.Contains("\"generated\":[\"a/Generated.g.cs\"]", json);
    }

    [Fact]
    public void ExplainResult_SerializesPlanCamelCase()
    {
        var explain = new ExplainResultDto(
            "focus", [new ExplainFileDto("a/B.cs", "Seed", "Standard", 3.14)]);

        var json = JsonSerializer.Serialize(explain, FuseHostJsonContext.Default.ExplainResultDto);

        Assert.Contains("\"mode\":\"focus\"", json);
        Assert.Contains("\"role\":\"Seed\"", json);
        Assert.Contains("\"tier\":\"Standard\"", json);
    }

    [Fact]
    public void CheckDelta_SerializesIntroducedAndResolvedCamelCase()
    {
        var delta = new CheckDeltaDto(
            true,
            [new CheckDiagnosticDto("CS1061", "Error", "'Order' does not contain a definition for 'Total'", "a/Order.cs", 12)],
            [new CheckDiagnosticDto("CS0246", "Error", "type 'Widget' not found", "a/Widget.cs", 3)]);

        var json = JsonSerializer.Serialize(delta, FuseHostJsonContext.Default.CheckDeltaDto);

        Assert.Contains("\"resident\":true", json);
        Assert.Contains("\"introduced\":[{\"id\":\"CS1061\",\"severity\":\"Error\"", json);
        Assert.Contains("\"path\":\"a/Order.cs\",\"line\":12", json);
        Assert.Contains("\"resolved\":[{\"id\":\"CS0246\"", json);
    }

    [Fact]
    public void CheckOverlayResult_SerializesDiagnosticsCamelCase()
    {
        var overlay = new CheckOverlayResultDto(
            true,
            [new CheckDiagnosticDto("CS0246", "Error", "type 'Widget' not found", "a/Widget.cs", 3)]);

        var json = JsonSerializer.Serialize(overlay, FuseHostJsonContext.Default.CheckOverlayResultDto);

        Assert.Contains("\"hasResident\":true", json);
        Assert.Contains("\"diagnostics\":[{\"id\":\"CS0246\",\"severity\":\"Error\"", json);
        Assert.Contains("\"path\":\"a/Widget.cs\",\"line\":3", json);
    }
}
