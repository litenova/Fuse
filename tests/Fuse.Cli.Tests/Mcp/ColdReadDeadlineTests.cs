using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

public sealed class ColdReadDeadlineTests
{
    [Fact]
    public void DeadlineMilliseconds_HonorsEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable(ColdReadDeadline.DeadlineEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ColdReadDeadline.DeadlineEnvVar, "1234");
            Assert.Equal(1234, ColdReadDeadline.DeadlineMilliseconds());
            Environment.SetEnvironmentVariable(ColdReadDeadline.DeadlineEnvVar, null);
            Assert.Equal(ColdReadDeadline.DefaultDeadlineMilliseconds, ColdReadDeadline.DeadlineMilliseconds());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ColdReadDeadline.DeadlineEnvVar, original);
        }
    }
}
