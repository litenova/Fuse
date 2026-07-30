using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Stops a spawned child from inheriting this process's standard handles for the duration of the spawn.
/// </summary>
/// <remarks>
///     On Windows .NET always calls <c>CreateProcess</c> with <c>bInheritHandles: true</c>, so every inheritable
///     handle in this process is duplicated into the child even when the child's own standard streams are
///     redirected. For a detached child that outlives its launcher, the duplicate of the launcher's stdout keeps
///     the caller's pipe open for the child's whole lifetime, so a piped or redirected command appears to hang
///     long after it finished. Clearing the inherit flag around the spawn removes exactly those duplicates. On
///     other platforms the runtime does not leak them, so this is a no-op.
///     <para>
///         The flag is process-wide while the scope is open. The window is one <c>Process.Start</c> call, and the
///         other processes Fuse spawns redirect their own streams, so a concurrent spawn is unaffected.
///     </para>
/// </remarks>
internal readonly struct InheritedStandardHandleScope : IDisposable
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const int HandleFlagInherit = 0x00000001;

    private readonly (nint Handle, int Flags)[]? _restore;

    private InheritedStandardHandleScope((nint Handle, int Flags)[]? restore) => _restore = restore;

    /// <summary>Clears the inherit flag on this process's standard handles until the scope is disposed.</summary>
    /// <returns>The scope that restores the previous flags.</returns>
    internal static InheritedStandardHandleScope Suppress()
    {
        if (!OperatingSystem.IsWindows())
            return new InheritedStandardHandleScope(null);

        var restore = new List<(nint, int)>(3);
        foreach (var id in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(id);
            if (handle == nint.Zero || handle == -1 || !GetHandleInformation(handle, out var flags))
                continue;
            if ((flags & HandleFlagInherit) == 0)
                continue;
            if (SetHandleInformation(handle, HandleFlagInherit, 0))
                restore.Add((handle, flags));
        }

        return new InheritedStandardHandleScope(restore.Count == 0 ? null : restore.ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_restore is null || !OperatingSystem.IsWindows())
            return;

        foreach (var (handle, flags) in _restore)
            SetHandleInformation(handle, HandleFlagInherit, flags & HandleFlagInherit);
    }

    // DllImport rather than LibraryImport: the source generator requires unsafe code, and these three signatures
    // are blittable enough that the generated marshalling would buy nothing.
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint hObject, out int lpdwFlags);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint hObject, int dwMask, int dwFlags);
}
