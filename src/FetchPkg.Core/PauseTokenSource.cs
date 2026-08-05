namespace FetchPkg.Core;

public sealed class PauseTokenSource
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _resumeSource;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumeSource is not null;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _resumeSource ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? source;
        lock (_gate)
        {
            source = _resumeSource;
            _resumeSource = null;
        }

        source?.TrySetResult(true);
    }

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_gate)
        {
            task = _resumeSource?.Task ?? Task.CompletedTask;
        }

        return task.WaitAsync(cancellationToken);
    }
}
