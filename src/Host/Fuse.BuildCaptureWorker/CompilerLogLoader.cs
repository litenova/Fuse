using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Loads C# compiler invocations from a binary or portable compiler log into eager or lazy held compilations.
/// </summary>
internal sealed class CompilerLogLoader
{
    private readonly Func<Compilation, string?, Compilation> _normalizeSigning;

    internal CompilerLogLoader(Func<Compilation, string?, Compilation> normalizeSigning) =>
        _normalizeSigning = normalizeSigning;

    internal HeldComplog LoadHeld(string logPath, CancellationToken cancellationToken)
    {
        var reader = CompilerCallReaderUtil.Create(logPath);
        var compilations = new List<Compilation>();
        try
        {
            foreach (var data in reader.ReadAllCompilationData())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.CompilerCall.IsCSharp != true)
                    continue;
                compilations.Add(_normalizeSigning(
                    data.GetCompilationAfterGenerators(cancellationToken),
                    data.CompilerCall.ProjectFilePath));
            }
        }
        catch
        {
            reader.Dispose();
            throw;
        }

        return new HeldComplog(reader, compilations);
    }

    internal LazyHeldComplog LoadLazyHeld(string logPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = CompilerCallReaderUtil.Create(logPath);
        try
        {
            var calls = reader.ReadAllCompilationData()
                .Where(static data => data.CompilerCall.IsCSharp == true)
                .ToList();
            var sourcePaths = calls
                .Select(data => reader.ReadAllSourceTextData(data.CompilerCall)
                    .Select(source => source.FilePath.Replace('\\', '/'))
                    .ToList())
                .ToList();
            return new LazyHeldComplog(reader, calls, sourcePaths);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }
}
