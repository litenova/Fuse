using Microsoft.Build.Locator;

namespace Fuse.Semantics;

/// <summary>
///     Registers MSBuild once per process. Every semantic entry point uses this gate because MSBuild assemblies
///     must be registered before they are loaded, and separate locks cannot protect that process-wide invariant.
/// </summary>
internal static class MsBuildLocatorRegistration
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered || MSBuildLocator.IsRegistered)
            {
                _registered = true;
                return;
            }

            MSBuildLocator.RegisterDefaults();
            _registered = true;
        }
    }
}

// The default warm-cache loader owns MSBuild registration. Refactor operations catch this marker so they retain
// their actionable abstention while injected caches can serve an already-loaded Roslyn solution without an SDK.
internal sealed class MsBuildLocatorUnavailableException(Exception innerException) : Exception(innerException.Message, innerException)
{
}
