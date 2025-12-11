namespace Fuse.Core.Abstractions;

public interface IFuseService
{
    Task FuseAsync(FuseOptions options, CancellationToken cancellationToken = default);
}