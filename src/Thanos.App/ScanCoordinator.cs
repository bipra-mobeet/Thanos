using Thanos.Core;

namespace Thanos.App;

public sealed class ScanCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task<ScanSummary>? _activeTask;

    public event Action<ScanProgress>? ProgressChanged;
    public ScanProgress LastProgress { get; private set; } = new(ScanPhase.Idle, 0, 0);

    public Task<ScanSummary>? ActiveTask
    {
        get { lock (_gate) return _activeTask; }
    }

    public bool IsRunning => ActiveTask is { IsCompleted: false };

    public Task<ScanSummary> StartAsync(IReadOnlyCollection<ScanFolder> folders, float threshold)
    {
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
                throw new InvalidOperationException("A scan is already running.");

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _activeTask = RunAsync(folders.ToArray(), threshold, _cancellation.Token);
            return _activeTask;
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    private async Task<ScanSummary> RunAsync(IReadOnlyCollection<ScanFolder> folders, float threshold, CancellationToken cancellationToken)
    {
        var progress = new Progress<ScanProgress>(value =>
        {
            LastProgress = value;
            ProgressChanged?.Invoke(value);
        });

        try
        {
            var service = await AppServices.CreateScanServiceAsync();
            return await Task.Run(() => service.ScanAsync(folders, threshold, progress, cancellationToken), cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }
}
