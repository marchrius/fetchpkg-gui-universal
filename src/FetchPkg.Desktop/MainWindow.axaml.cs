using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FetchPkg.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace FetchPkg.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadJob> _jobs = new();
    private readonly HashSet<DownloadJob> _pending = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private Task _queueTask = Task.CompletedTask;
    private bool _closing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        JobsGrid.ItemsSource = _jobs;
        OutputBox.Text = _settings.OutputDirectory;
        ConnectionsBox.Value = Math.Clamp(_settings.MaxConnections, 1, 64);
        SegmentBox.Value = Math.Clamp(_settings.SegmentSizeMiB, 1, 1024);
        SparseBox.IsChecked = _settings.UseSparseFiles;
        VerifyBox.IsChecked = _settings.VerifyHashes;
        IgnoreSslBox.IsChecked = _settings.IgnoreSslErrors;
        ProxyBox.Text = _settings.ProxyUrl;
        Closing += OnClosing;
    }

    private void AddLinks(object? sender, RoutedEventArgs e) => AddText(SourceBox.Text);

    private async void PasteLinks(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not null)
                AddText(await Clipboard.TryGetTextAsync());
        }
        catch (Exception ex) { StatusText.Text = $"Cannot read clipboard: {ex.Message}"; }
    }

    private void AddText(string? text)
    {
        if (_closing) return;
        var added = 0;
        var errors = new List<string>();
        foreach (var line in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (!Uri.TryCreate(line, UriKind.Absolute, out var source) ||
                    source.Scheme is not ("http" or "https"))
                    throw new ArgumentException("Enter HTTP/HTTPS JSON or PKG links, one per line.");
                source = ManifestUrl.Normalize(source);
                if (ManifestUrl.IsNumberedPackagePiece(source) ||
                    (ManifestUrl.IsPs5ApplicationPackage(source) &&
                     !source.AbsolutePath.EndsWith("-DP.pkg", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("For package pieces, copy the JSON or _sc.pkg manifest link instead.");
                if (!source.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !source.AbsolutePath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Use a JSON manifest, SC link, or standalone PKG URL.");
                if (_jobs.Any(job => job.Source == source))
                    throw new ArgumentException("This package is already in the queue.");
                if (string.IsNullOrWhiteSpace(OutputBox.Text))
                    throw new ArgumentException("Choose an output folder first.");
                var directory = Path.GetFullPath(OutputBox.Text.Trim());
                var path = ResolveOutputPath(Path.Combine(directory, ManifestUrl.SuggestFileName(source)), source);
                var job = new DownloadJob(source, path);
                _jobs.Add(job);
                JobsGrid.SelectedItem = job;
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                errors.Add(ex.Message);
            }
        }
        if (errors.Count == 0 && added > 0) SourceBox.Text = "";
        StatusText.Text = $"Added {added} package(s)." +
                          (errors.Count > 0 ? $" Skipped {errors.Count}: {string.Join(" ", errors.Distinct())}" : "");
    }

    private string ResolveOutputPath(string preferred, Uri source)
    {
        bool InUse(string path) => _jobs.Any(job => string.Equals(job.OutputPath, path, StringComparison.OrdinalIgnoreCase));
        bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        if (!InUse(preferred) && (!Exists(preferred) || CanResume(preferred, source))) return preferred;
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(Path.GetDirectoryName(preferred)!,
                $"{Path.GetFileNameWithoutExtension(preferred)} ({index}){Path.GetExtension(preferred)}");
            if (!InUse(candidate) && !Exists(candidate)) return candidate;
        }
    }

    private static bool CanResume(string path, Uri source)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(path + ".fetchpkg.state.json"));
            return state.RootElement.TryGetProperty("ManifestUrl", out var url) &&
                   url.ValueKind == JsonValueKind.String && url.GetString() == source.AbsoluteUri;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private async void ChooseFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose download folder", AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) OutputBox.Text = path;
        }
        catch (Exception ex) { StatusText.Text = $"Cannot choose folder: {ex.Message}"; }
    }

    private void OpenPatches(object? sender, RoutedEventArgs e) => OpenExternal("https://www.prosperopatches.com/");

    private void OpenFolder(object? sender, RoutedEventArgs e)
    {
        var job = Selected().FirstOrDefault();
        var folder = job is null ? OutputBox.Text : Path.GetDirectoryName(job.OutputPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) OpenExternal(Path.GetFullPath(folder));
        else StatusText.Text = "The output folder does not exist yet.";
    }

    private void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Cannot open {target}: {ex.Message}"; }
    }

    private List<DownloadJob> Selected() => JobsGrid.SelectedItems.OfType<DownloadJob>().ToList();
    private void PauseSelected(object? sender, RoutedEventArgs e) { foreach (var job in Selected()) job.Pause(); }
    private void ResumeSelected(object? sender, RoutedEventArgs e) { foreach (var job in Selected()) job.Resume(); }

    private void CancelSelected(object? sender, RoutedEventArgs e)
    {
        foreach (var job in Selected())
        {
            _pending.Remove(job);
            job.Cancel();
        }
        StatusText.Text = "Selected downloads cancelled or removed from the pending run. Partial files are preserved.";
    }

    private void RemoveSelected(object? sender, RoutedEventArgs e)
    {
        foreach (var job in Selected().Where(job => !job.IsRunning))
        {
            _pending.Remove(job);
            _jobs.Remove(job);
        }
    }

    private async void StartSelected(object? sender, RoutedEventArgs e)
    {
        if (_closing || !_queueTask.IsCompleted) return;
        var jobs = Selected();
        if (jobs.Count == 0) jobs = _jobs.ToList();
        jobs = jobs.Where(job => job.Status != "Completed" && !job.IsRunning).ToList();
        if (jobs.Count == 0) { StatusText.Text = "No unfinished packages to start."; return; }
        try
        {
            var options = ReadOptions();
            SaveSettings();
            _pending.UnionWith(jobs);
            _queueTask = RunQueueAsync(jobs, options);
            await _queueTask;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task RunQueueAsync(List<DownloadJob> jobs, DownloadOptions options)
    {
        try
        {
            foreach (var job in jobs)
            {
                if (_closing) break;
                if (!_pending.Remove(job)) continue;
                StatusText.Text = $"Downloading {job.Name}";
                await job.StartAsync(options);
            }
        }
        finally
        {
            _pending.Clear();
            if (!_closing) StatusText.Text = "Queue finished. Check each package status for results.";
        }
    }

    private DownloadOptions ReadOptions()
    {
        var proxy = ProxyBox.Text?.Trim();
        if (!string.IsNullOrEmpty(proxy) &&
            (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")))
            throw new ArgumentException("Enter a valid HTTP, HTTPS or SOCKS proxy URL.");
        return new DownloadOptions
        {
            MaxConnections = (int)(ConnectionsBox.Value ?? 8),
            SegmentSizeBytes = (long)(SegmentBox.Value ?? 16) * 1024 * 1024,
            UseSparseFile = SparseBox.IsChecked == true,
            VerifyPieceHashes = VerifyBox.IsChecked == true,
            IgnoreSslErrors = IgnoreSslBox.IsChecked == true,
            ProxyUrl = string.IsNullOrEmpty(proxy) ? null : proxy,
            Resume = true
        };
    }

    private void SaveSettings()
    {
        _settings.OutputDirectory = OutputBox.Text?.Trim() ?? "";
        _settings.MaxConnections = (int)(ConnectionsBox.Value ?? 8);
        _settings.SegmentSizeMiB = (int)(SegmentBox.Value ?? 16);
        _settings.UseSparseFiles = SparseBox.IsChecked == true;
        _settings.VerifyHashes = VerifyBox.IsChecked == true;
        _settings.IgnoreSslErrors = IgnoreSslBox.IsChecked == true;
        _settings.ProxyUrl = ProxyBox.Text?.Trim() ?? "";
        _settings.Save();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        SaveSettings();
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        StatusText.Text = "Saving download state and closing…";
        _pending.Clear();
        foreach (var job in _jobs) job.Cancel();
        try { await _queueTask; }
        finally
        {
            _allowClose = true;
            // Even an empty queue completes synchronously. Let the current
            // Closing event return before requesting the final close.
            Dispatcher.UIThread.Post(Close);
        }
    }
}
