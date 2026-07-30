using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Optional named-pipe access control for <c>fuse host</c> on Windows. When
///     <c>FUSE_HOST_RESTRICT_PIPE=1</c>, the pipe security descriptor grants read/write only to the
///     process owner's user SID instead of the platform default that allows any same-session client.
/// </summary>
public static class HostPipeSecurity
{
    /// <summary>The environment variable that opts into a current-user-only pipe ACL.</summary>
    public const string EnvironmentVariable = "FUSE_HOST_RESTRICT_PIPE";

    /// <summary>
    ///     Whether <see cref="EnvironmentVariable" /> requests a current-user-only pipe ACL on Windows.
    /// </summary>
    /// <returns>True when the variable is set to a truthy value (1, true, yes, or on).</returns>
    public static bool RestrictToCurrentUserOptIn()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Creates a named-pipe server stream for <paramref name="pipeName" />. On Windows with
    ///     <see cref="RestrictToCurrentUserOptIn" /> true, the ACL allows only the current user; otherwise the
    ///     platform default applies.
    /// </summary>
    /// <param name="pipeName">The bare pipe name (without the <c>\\.\pipe\</c> prefix).</param>
    /// <returns>A server stream ready for <c>WaitForConnectionAsync</c>.</returns>
    public static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        if (!OperatingSystem.IsWindows() || !RestrictToCurrentUserOptIn())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        var pipeSecurity = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                user,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        }

        // Drop inherited Everyone / Authenticated Users grants from the default descriptor.
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }
}
