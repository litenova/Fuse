using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Bounds the compiler state a daemon owns for one repository root. A default budget of one means a resident
///     workspace, warm MSBuild solution, or pooled capture worker is the sole held compiler state at a time.
/// </summary>
internal sealed class CompilerStateBudget
{
    private enum State
    {
        Warm,
        Capture,
        Resident,
    }

    private readonly string _root;
    private readonly int _cap;
    private readonly object _gate = new();
    private readonly LinkedList<State> _lru = new();
    private readonly Dictionary<State, LinkedListNode<State>> _lruIndex = new();
    private readonly WarmSolutionCache _warmSolutions;
    private readonly PooledCheckWorker _pooledWorkers;
    private readonly IResidentWorkspaceProvider _residentWorkspaces;

    public CompilerStateBudget(
        string root,
        int? cap = null,
        WarmSolutionCache? warmSolutions = null,
        PooledCheckWorker? pooledWorkers = null,
        IResidentWorkspaceProvider? residentWorkspaces = null)
    {
        _root = Path.GetFullPath(root);
        _cap = cap ?? ReadCap();
        _warmSolutions = warmSolutions ?? new WarmSolutionCache();
        _pooledWorkers = pooledWorkers ?? new PooledCheckWorker();
        _residentWorkspaces = residentWorkspaces ?? NullResidentWorkspaceProvider.Instance;
    }

    public void ActivateWarm() => Activate(State.Warm);

    public void ActivateCapture() => Activate(State.Capture);

    public void ActivateResident() => Activate(State.Resident);

    private void Activate(State state)
    {
        lock (_gate)
        {
            if (_lruIndex.Remove(state, out var existing))
                _lru.Remove(existing);
            _lruIndex[state] = _lru.AddFirst(state);

            while (_lru.Count > _cap && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _lruIndex.Remove(oldest.Value);
                Evict(oldest.Value);
            }
        }
    }

    private static int ReadCap()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_COMPILER_STATE_CAP");
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 1;
    }

    private void Evict(State state)
    {
        switch (state)
        {
            case State.Warm:
                _warmSolutions.EvictUnderRoot(_root);
                break;
            case State.Capture:
                _pooledWorkers.EvictOwnedBy(_root);
                break;
            case State.Resident:
                EvictResident();
                break;
        }
    }

    private void EvictResident()
    {
        if (_residentWorkspaces is ResidentWorkspaceRegistry registry)
            registry.Evict(_root);
    }
}
