using FetchPkg.Core;
using Avalonia.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FetchPkg.Desktop;

internal sealed class DownloadJob : INotifyPropertyChanged
{
    private CancellationTokenSource? _cancellation;
    private PauseTokenSource? _pause;
    private Task? _runTask;

    private string _status = "Queued";
    private double _progress;
    private string _speed = "—";
    private string _eta = "—";
    private string _details = string.Empty;

    public DownloadJob(Uri source, string outputPath)
    {
        Source = source;
        OutputPath = outputPath;
        Name = Path.GetFileName(outputPath);
    }

    public Uri Source { get; }
    public string SourceUrl => Source.AbsoluteUri;
    public string OutputPath { get; }
    public string Name { get; }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetField(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => $"{Progress:0.00}%";

    public string Speed
    {
        get => _speed;
        private set => SetField(ref _speed, value);
    }

    public string Eta
    {
        get => _eta;
        private set => SetField(ref _eta, value);
    }

    public string Details
    {
        get => _details;
        private set => SetField(ref _details, value);
    }

    public bool IsRunning => _runTask is { IsCompleted: false };
    public bool IsPaused => _pause?.IsPaused == true;
    public Task Completion => _runTask ?? Task.CompletedTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task StartAsync(DownloadOptions options)
    {
        if (IsRunning || Status == "Completed")
        {
            return Completion;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        _runTask = RunAsync(options, _cancellation.Token);
        return _runTask;
    }

    public void Pause()
    {
        if (!IsRunning || _pause is null)
        {
            return;
        }

        _pause.Pause();
        Post(() => Status = "Paused");
    }

    public void Resume()
    {
        if (!IsRunning || _pause is null)
        {
            return;
        }

        _pause.Resume();
        Post(() => Status = "Downloading");
    }

    public void Cancel()
    {
        _pause?.Resume();
        _cancellation?.Cancel();
    }

    private async Task RunAsync(DownloadOptions options, CancellationToken cancellationToken)
    {
        Post(() =>
        {
            Status = "Preparing";
            Details = Source.Host;
            Speed = "—";
            Eta = "—";
        });

        try
        {
            using var http = HttpClientBuilder.Create(options);
            var downloader = new PackageDownloader(http);
            var progress = new UiProgress(value =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    OnProgress(value);
            });
            var result = await downloader.DownloadAsync(
                Source,
                OutputPath,
                options,
                progress,
                _pause,
                cancellationToken);

            Post(() =>
            {
                Progress = 100;
                Status = "Completed";
                Speed = FormatRate(result.LogicalSize / Math.Max(0.001, result.Elapsed.TotalSeconds));
                Eta = "Done";
                var storage = result.SparseFileEnabled
                    ? (OperatingSystem.IsWindows() ? "sparse" : "sparse strategy")
                    : "regular";
                Details = result.Resumed
                    ? $"Resumed; {result.PieceCount} piece(s); {storage} file"
                    : $"{result.PieceCount} piece(s); {storage} file";
            });
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                Status = "Cancelled";
                Eta = "—";
                Details = "Partial data and resume state were preserved.";
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                Status = "Error";
                Eta = "—";
                Details = ex.Message;
            });
        }
        finally
        {
            _pause?.Resume();
            Post(() => OnPropertyChanged(nameof(IsRunning)));
        }
    }

    private void OnProgress(DownloadProgress value)
    {
        Status = value.Stage switch
        {
            DownloadStage.Preparing => "Preparing",
            DownloadStage.Downloading when IsPaused => "Paused",
            DownloadStage.Downloading => "Downloading",
            DownloadStage.Verifying => "Verifying",
            DownloadStage.Completed => "Completed",
            _ => Status
        };
        Progress = value.Percent;
        Speed = value.BytesPerSecond > 0 ? FormatRate(value.BytesPerSecond) : "—";
        Eta = value.EstimatedRemaining is { } eta ? FormatDuration(eta) : "—";
        Details = value.CurrentItem ?? string.Empty;
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024 * 1024 * 1024):0.00} GiB/s";
        }

        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024 * 1024):0.00} MiB/s";
        }

        return $"{bytesPerSecond / 1024:0.00} KiB/s";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{value.Minutes}m {value.Seconds}s";
        }

        return $"{Math.Max(0, value.Seconds)}s";
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private sealed class UiProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => Post(() => report(value));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
