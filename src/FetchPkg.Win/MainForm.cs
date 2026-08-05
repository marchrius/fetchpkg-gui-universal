using FetchPkg.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace FetchPkg.Win;

internal sealed class MainForm : Form
{
    private readonly BindingList<DownloadJob> _jobs = new();
    private readonly AppSettings _settings;

    private readonly TextBox _sourceBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputFolderBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _proxyBox = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 64, Width = 70 };
    private readonly NumericUpDown _segmentSize = new() { Minimum = 1, Maximum = 1024, Width = 80 };
    private readonly CheckBox _sparse = new() { Text = "Sparse files", AutoSize = true };
    private readonly CheckBox _verify = new() { Text = "Verify piece hashes", AutoSize = true };
    private readonly CheckBox _ignoreSsl = new() { Text = "Ignore SSL errors (unsafe)", AutoSize = true };
    private readonly DataGridView _grid = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Ready");
    private bool _queueLoopRunning;
    private bool _shutdownInProgress;
    private bool _allowClose;

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "FetchPkg for Windows";
        Width = 1280;
        Height = 760;
        MinimumSize = new Size(980, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        BuildInterface();
        LoadSettingsIntoControls();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += OnFormClosing;
    }

    private void BuildInterface()
    {
        var addButton = new Button { Text = "Add", AutoSize = true };
        var patchesButton = new Button { Text = "PROSPEROPatches…", AutoSize = true };
        var browseButton = new Button { Text = "Browse…", AutoSize = true };
        addButton.Click += (_, _) => AddFromSourceBox();
        patchesButton.Click += (_, _) => OpenProsperoBrowser();
        browseButton.Click += (_, _) => BrowseOutputFolder();
        _sourceBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddFromSourceBox();
            }
        };

        var sourcePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            ColumnCount = 4,
            Padding = new Padding(8, 7, 8, 4)
        };
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourcePanel.Controls.Add(new Label
        {
            Text = "Manifest or PKG URL:",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, 0);
        sourcePanel.Controls.Add(_sourceBox, 1, 0);
        sourcePanel.Controls.Add(addButton, 2, 0);
        sourcePanel.Controls.Add(patchesButton, 3, 0);

        var settingsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            ColumnCount = 8,
            RowCount = 2,
            Padding = new Padding(8, 3, 8, 7)
        };
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        settingsPanel.Controls.Add(new Label { Text = "Output folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        settingsPanel.Controls.Add(_outputFolderBox, 1, 0);
        settingsPanel.Controls.Add(browseButton, 2, 0);
        settingsPanel.Controls.Add(new Label { Text = "Connections:", AutoSize = true, Anchor = AnchorStyles.Left }, 3, 0);
        settingsPanel.Controls.Add(_connections, 4, 0);
        settingsPanel.Controls.Add(new Label { Text = "Segment MiB:", AutoSize = true, Anchor = AnchorStyles.Left }, 5, 0);
        settingsPanel.Controls.Add(_segmentSize, 6, 0);
        settingsPanel.Controls.Add(_sparse, 7, 0);

        settingsPanel.Controls.Add(new Label { Text = "HTTP proxy:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        settingsPanel.Controls.Add(_proxyBox, 1, 1);
        settingsPanel.SetColumnSpan(_proxyBox, 2);
        settingsPanel.Controls.Add(_verify, 3, 1);
        settingsPanel.SetColumnSpan(_verify, 2);
        settingsPanel.Controls.Add(_ignoreSsl, 5, 1);
        settingsPanel.Controls.Add(new Label
        {
            Text = "Completed ranges are recorded in .fetchpkg.state.json for resume.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left
        }, 6, 1);
        settingsPanel.SetColumnSpan(settingsPanel.GetControlFromPosition(6, 1)!, 2);

        ConfigureGrid();

        var startButton = new Button { Text = "Start selected", AutoSize = true };
        var pauseButton = new Button { Text = "Pause", AutoSize = true };
        var resumeButton = new Button { Text = "Resume", AutoSize = true };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true };
        var removeButton = new Button { Text = "Remove", AutoSize = true };
        var openFolderButton = new Button { Text = "Open folder", AutoSize = true };

        startButton.Click += async (_, _) => await StartSelectedAsync();
        pauseButton.Click += (_, _) => ForSelected(job => job.Pause());
        resumeButton.Click += (_, _) => ForSelected(job => job.Resume());
        cancelButton.Click += (_, _) => ForSelected(job => job.Cancel());
        removeButton.Click += (_, _) => RemoveSelected();
        openFolderButton.Click += (_, _) => OpenSelectedFolder();

        var commandPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8)
        };
        commandPanel.Controls.Add(startButton);
        commandPanel.Controls.Add(pauseButton);
        commandPanel.Controls.Add(resumeButton);
        commandPanel.Controls.Add(cancelButton);
        commandPanel.Controls.Add(removeButton);
        commandPanel.Controls.Add(openFolderButton);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(commandPanel);
        Controls.Add(settingsPanel);
        Controls.Add(sourcePanel);
        Controls.Add(statusStrip);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.DataSource = _jobs;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "File",
            DataPropertyName = nameof(DownloadJob.Name),
            Width = 245
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Status",
            DataPropertyName = nameof(DownloadJob.Status),
            Width = 100
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Progress",
            DataPropertyName = nameof(DownloadJob.ProgressText),
            Width = 85
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Speed",
            DataPropertyName = nameof(DownloadJob.Speed),
            Width = 105
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "ETA",
            DataPropertyName = nameof(DownloadJob.Eta),
            Width = 85
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Details",
            DataPropertyName = nameof(DownloadJob.Details),
            Width = 260
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Source",
            DataPropertyName = nameof(DownloadJob.SourceUrl),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220
        });

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                OpenFolder(_jobs[e.RowIndex].OutputPath);
            }
        };
    }

    private void LoadSettingsIntoControls()
    {
        _outputFolderBox.Text = _settings.OutputDirectory;
        _connections.Value = Math.Clamp(_settings.MaxConnections, (int)_connections.Minimum, (int)_connections.Maximum);
        _segmentSize.Value = Math.Clamp(_settings.SegmentSizeMiB, (int)_segmentSize.Minimum, (int)_segmentSize.Maximum);
        _sparse.Checked = _settings.UseSparseFiles;
        _verify.Checked = _settings.VerifyHashes;
        _ignoreSsl.Checked = _settings.IgnoreSslErrors;
        _proxyBox.Text = _settings.ProxyUrl;
    }

    private void SaveControlsIntoSettings()
    {
        _settings.OutputDirectory = _outputFolderBox.Text.Trim();
        _settings.MaxConnections = (int)_connections.Value;
        _settings.SegmentSizeMiB = (int)_segmentSize.Value;
        _settings.UseSparseFiles = _sparse.Checked;
        _settings.VerifyHashes = _verify.Checked;
        _settings.IgnoreSslErrors = _ignoreSsl.Checked;
        _settings.ProxyUrl = _proxyBox.Text.Trim();
        _settings.Save();
    }

    private void AddFromSourceBox()
    {
        var text = _sourceBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var source) ||
            !source.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !source.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Enter a valid HTTP/HTTPS package manifest or PKG URL.",
                "Invalid URL",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        AddSource(source);
        _sourceBox.Clear();
    }

    private void AddSource(Uri source)
    {
        if (!IsHttpUri(source))
        {
            _statusLabel.Text = "Only HTTP and HTTPS package links are supported.";
            return;
        }

        if (ManifestUrl.TryGetManifestUri(source, out var manifestUri))
        {
            source = manifestUri;
        }
        else if (ManifestUrl.IsPs5ApplicationPackage(source) &&
                 !source.AbsolutePath.EndsWith("-DP.pkg", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "This PS5 /app/pkg/ link is an application package piece. Its JSON manifest " +
                "has a different /app/info/ revision and hash and cannot be generated from " +
                "this URL. Add the JSON or _sc.pkg link instead.",
                "Manifest URL required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        else if (ManifestUrl.IsNumberedPackagePiece(source))
        {
            MessageBox.Show(
                this,
                "This URL contains only one numbered PKG piece. Add the JSON manifest instead.",
                "Manifest URL required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var outputDirectory = _outputFolderBox.Text.Trim();
        if (outputDirectory.Length == 0)
        {
            BrowseOutputFolder();
            outputDirectory = _outputFolderBox.Text.Trim();
            if (outputDirectory.Length == 0)
            {
                return;
            }
        }

        if (_jobs.Any(job => job.Source == source))
        {
            _statusLabel.Text = "That package is already in the queue.";
            return;
        }

        var preferredPath = Path.Combine(outputDirectory, ManifestUrl.SuggestFileName(source));
        var outputPath = ResolveOutputPath(preferredPath, source);
        var job = new DownloadJob(source, outputPath);
        _jobs.Add(job);
        _grid.ClearSelection();
        _grid.Rows[_grid.Rows.Count - 1].Selected = true;
        _statusLabel.Text = $"Added {job.Name}";
    }

    private string ResolveOutputPath(string preferredPath, Uri source)
    {
        var pathConflict = _jobs.Any(job =>
            job.OutputPath.Equals(preferredPath, StringComparison.OrdinalIgnoreCase));
        var existingFileCanResume = File.Exists(preferredPath) && HasMatchingResumeState(preferredPath, source);

        if (!pathConflict && (!File.Exists(preferredPath) || existingFileCanResume))
        {
            return preferredPath;
        }

        var directory = Path.GetDirectoryName(preferredPath)!;
        var stem = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) &&
                !_jobs.Any(job => job.OutputPath.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private static bool HasMatchingResumeState(string outputPath, Uri source)
    {
        var statePath = outputPath + ".fetchpkg.state.json";
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("ManifestUrl", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var saved = value.GetString();
            return string.Equals(saved, source.AbsoluteUri, StringComparison.Ordinal) ||
                   string.Equals(saved, ManifestUrl.Normalize(source).AbsoluteUri, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsHttpUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private void OpenProsperoBrowser()
    {
        var browser = new ProsperoBrowserForm(_ignoreSsl.Checked);
        browser.PackageLinksSelected += (_, e) =>
        {
            foreach (var link in e.Links)
            {
                AddSource(link);
            }
        };
        browser.Show(this);
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder for downloaded PKG files",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_outputFolderBox.Text) ? _outputFolderBox.Text : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputFolderBox.Text = dialog.SelectedPath;
        }
    }

    private async Task StartSelectedAsync()
    {
        if (_queueLoopRunning)
        {
            _statusLabel.Text = "A queue start operation is already active.";
            return;
        }

        var jobs = GetSelectedJobs();
        if (jobs.Count == 0)
        {
            jobs = _jobs.Where(job => !job.IsRunning && job.Status != "Completed").ToList();
        }

        if (jobs.Count == 0)
        {
            _statusLabel.Text = "No queued jobs to start.";
            return;
        }

        SaveControlsIntoSettings();
        var options = BuildDownloadOptions();
        _queueLoopRunning = true;
        try
        {
            foreach (var job in jobs)
            {
                _statusLabel.Text = $"Running {job.Name}";
                await job.StartAsync(options);
            }
        }
        finally
        {
            _queueLoopRunning = false;
            _statusLabel.Text = "Queue operation finished.";
        }
    }

    private DownloadOptions BuildDownloadOptions()
    {
        return new DownloadOptions
        {
            MaxConnections = (int)_connections.Value,
            SegmentSizeBytes = (long)_segmentSize.Value * 1024 * 1024,
            RetryCount = 3,
            UseSparseFile = _sparse.Checked,
            Resume = true,
            VerifyPieceHashes = _verify.Checked,
            IgnoreSslErrors = _ignoreSsl.Checked,
            ProxyUrl = string.IsNullOrWhiteSpace(_proxyBox.Text) ? null : _proxyBox.Text.Trim()
        };
    }

    private List<DownloadJob> GetSelectedJobs()
    {
        return _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<DownloadJob>()
            .Distinct()
            .ToList();
    }

    private void ForSelected(Action<DownloadJob> action)
    {
        foreach (var job in GetSelectedJobs())
        {
            action(job);
        }
    }

    private void RemoveSelected()
    {
        foreach (var job in GetSelectedJobs())
        {
            if (job.IsRunning)
            {
                continue;
            }

            _jobs.Remove(job);
        }
    }

    private void OpenSelectedFolder()
    {
        var job = GetSelectedJobs().FirstOrDefault();
        if (job is not null)
        {
            OpenFolder(job.OutputPath);
        }
    }

    private static void OpenFolder(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
            {
                UseShellExecute = true
            });
            return;
        }

        var folder = Path.GetDirectoryName(fullPath);
        if (folder is not null && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.Text) == true ||
            e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var text = e.Data?.GetData(DataFormats.Text) as string;
        if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri))
        {
            AddSource(uri);
            return;
        }

        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(file => file.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
            {
                var urlLine = File.ReadLines(file).FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
                if (urlLine is not null && Uri.TryCreate(urlLine[4..], UriKind.Absolute, out uri))
                {
                    AddSource(uri);
                }
            }
        }
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            SaveControlsIntoSettings();
            return;
        }

        var activeJobs = _jobs.Where(job => job.IsRunning).ToArray();
        if (activeJobs.Length == 0)
        {
            SaveControlsIntoSettings();
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Downloads are still active. Cancel them and close? Resume state will be preserved.",
            "Active downloads",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return;
        }

        _shutdownInProgress = true;
        _statusLabel.Text = "Cancelling active downloads…";
        foreach (var job in activeJobs)
        {
            job.Cancel();
        }

        await Task.WhenAll(activeJobs.Select(job => job.Completion));
        await Task.Yield();
        _allowClose = true;
        Close();
    }
}
