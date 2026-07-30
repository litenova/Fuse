using System.CommandLine;
using System.Reflection;
using Fuse.Cli;
using Xunit;
using DotMakeCli = DotMake.CommandLine.Cli;

namespace Fuse.Cli.Tests.Commands;

// Regression guard (C3): `fuse index <path>` with no options must parse cleanly. An optional CLI option declared
// without `Required = false` was treated as required by DotMake, which broke the most basic index invocation
// ("Option '--from-capture' is required"). DotMake's Cli.Parse returns a runnable result that hides the underlying
// ParseResult, so these tests reflect into it and assert no parse errors - catching a future option added without
// Required = false before it ships.
// Shares the DotMakeCliParse collection with McpInstallTests: both build the DotMake command tree via
// Cli.Parse<FuseCliCommand>, and concurrent builds race on DotMake's process-global command registry. Serialize them.
[Collection("DotMakeCliParse")]
public sealed class IndexCommandParseTests
{
    private static IReadOnlyList<string> ParseErrors(params string[] args)
    {
        var runnable = DotMakeCli.Parse<FuseCliCommand>(args);
        var parseResult = FindParseResult(runnable);
        Assert.NotNull(parseResult);
        return parseResult!.Errors.Select(e => e.Message).ToList();
    }

    // The DotMake runnable result wraps a System.CommandLine ParseResult in a non-public member; find it by type.
    private static ParseResult? FindParseResult(object runnable)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in runnable.GetType().GetFields(flags))
        {
            if (field.GetValue(runnable) is ParseResult fromField)
                return fromField;
        }

        foreach (var prop in runnable.GetType().GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length == 0 && prop.GetValue(runnable) is ParseResult fromProp)
                return fromProp;
        }

        return null;
    }

    [Fact]
    public void Index_with_only_a_path_parses_without_errors()
        => Assert.Empty(ParseErrors("index", "some/workspace"));

    [Fact]
    public void Index_with_no_arguments_parses_without_errors()
        => Assert.Empty(ParseErrors("index"));

    [Fact]
    public void Index_with_from_capture_parses_without_errors()
        => Assert.Empty(ParseErrors("index", "some/workspace", "--from-capture", "some/bundle"));

    [Fact]
    public void Index_with_semantic_and_json_parses_without_errors()
        => Assert.Empty(ParseErrors("index", "some/workspace", "--semantic", "--json"));

    [Theory]
    [InlineData("status")]
    [InlineData("cancel")]
    [InlineData("clean", "--yes")]
    public void Index_lifecycle_subcommand_parses_without_errors(params string[] arguments)
        => Assert.Empty(ParseErrors(["index", .. arguments]));
}
