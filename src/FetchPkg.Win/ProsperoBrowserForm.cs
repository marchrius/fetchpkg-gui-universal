using FetchPkg.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FetchPkg.Win;

internal sealed class ProsperoBrowserForm : Form
{
    private static readonly Regex TitleIdRegex = new(@"\bPPSA\d{5}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill };
    private readonly bool _ignoreSslErrors;
    private string? _pendingSearch;

    public ProsperoBrowserForm(bool ignoreSslErrors)
    {
        _ignoreSslErrors = ignoreSslErrors;
        Text = "PROSPEROPatches browser";
        Width = 1280;
        Height = 850;
        StartPosition = FormStartPosition.CenterParent;

        var searchButton = new Button { Text = "Search", AutoSize = true };
        var collectButton = new Button { Text = "Find download links", AutoSize = true };
        var homeButton = new Button { Text = "Home", AutoSize = true };
        var closeButton = new Button { Text = "Close", AutoSize = true };

        searchButton.Click += async (_, _) => await SearchAsync();
        collectButton.Click += async (_, _) => await CollectLinksAsync();
        homeButton.Click += (_, _) => Navigate(new Uri("https://www.prosperopatches.com/"));
        closeButton.Click += (_, _) => Close();
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SearchAsync();
            }
        };

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 5,
            Padding = new Padding(6)
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.Controls.Add(_searchBox, 0, 0);
        toolbar.Controls.Add(searchButton, 1, 0);
        toolbar.Controls.Add(collectButton, 2, 0);
        toolbar.Controls.Add(homeButton, 3, 0);
        toolbar.Controls.Add(closeButton, 4, 0);

        Controls.Add(_webView);
        Controls.Add(toolbar);
        Shown += async (_, _) => await InitializeBrowserAsync();
    }

    public event EventHandler<PackageLinksSelectedEventArgs>? PackageLinksSelected;

    private async Task InitializeBrowserAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = true;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _webView.CoreWebView2.DownloadStarting += OnDownloadStarting;
            if (_ignoreSslErrors)
            {
                _webView.CoreWebView2.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
            }
            Navigate(new Uri("https://www.prosperopatches.com/"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "WebView2 could not be initialized. Install the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "WebView2 error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void OnServerCertificateErrorDetected(
        object? sender,
        CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    private async Task SearchAsync()
    {
        var query = _searchBox.Text.Trim();
        if (query.Length == 0 || _webView.CoreWebView2 is null)
        {
            return;
        }

        var id = TitleIdRegex.Match(query);
        if (id.Success)
        {
            Navigate(new Uri("https://www.prosperopatches.com/" + id.Value.ToUpperInvariant()));
            return;
        }

        _pendingSearch = query;
        var current = _webView.Source;
        if (current is null || !current.Host.EndsWith("prosperopatches.com", StringComparison.OrdinalIgnoreCase) || current.AbsolutePath != "/")
        {
            Navigate(new Uri("https://www.prosperopatches.com/"));
            return;
        }

        await InjectSearchAsync(query);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || string.IsNullOrWhiteSpace(_pendingSearch))
        {
            return;
        }

        var query = _pendingSearch;
        _pendingSearch = null;
        await InjectSearchAsync(query);
    }

    private async Task InjectSearchAsync(string query)
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        var literal = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const q = {{literal}};
              const inputs = [...document.querySelectorAll('input')];
              const input = inputs.find(x => /title name|title id|search database/i.test(
                  `${x.placeholder || ''} ${x.getAttribute('aria-label') || ''}`))
                  || inputs.find(x => x.type === 'search')
                  || inputs.find(x => x.type === 'text');
              if (!input) return 'input-not-found';
              input.focus();
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
              if (setter) setter.call(input, q); else input.value = q;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.dispatchEvent(new Event('change', { bubbles: true }));
              input.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true
              }));
              input.dispatchEvent(new KeyboardEvent('keyup', {
                key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true
              }));
              return 'ok';
            })();
            """;

        await _webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task CollectLinksAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        const string script = """
            (() => {
              const good = u => /(^|\.)playstation\.net$/i.test(new URL(u).hostname)
                                && /\.(pkg|json)(?:$|\?)/i.test(u);
              const map = new Map();
              for (const a of document.querySelectorAll('a[href]')) {
                let url;
                try { url = new URL(a.href, document.baseURI).href; } catch { continue; }
                if (!good(url)) continue;
                const parent = a.closest('tr, article, li, section, .card, div');
                const text = (a.innerText || parent?.innerText || url)
                    .replace(/\s+/g, ' ').trim().slice(0, 300);
                if (!map.has(url)) map.set(url, { text, url });
              }
              return JSON.stringify([...map.values()]);
            })();
            """;

        var raw = await _webView.CoreWebView2.ExecuteScriptAsync(script);
        var json = JsonSerializer.Deserialize<string>(raw) ?? "[]";
        var rawLinks = JsonSerializer.Deserialize<List<BrowserLink>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<BrowserLink>();

        var links = rawLinks
            .Select(CanonicalizeBrowserLink)
            .Where(link => link is not null)
            .Cast<BrowserLink>()
            .GroupBy(link => link.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (links.Count == 0)
        {
            MessageBox.Show(
                this,
                "No direct Sony PKG/JSON links were found on the current page. Open an update or DLC entry first, then try again.",
                "No links",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var picker = new LinkPickerForm(links);
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var selected = picker.SelectedLinks
            .Select(link => Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null)
            .Cast<Uri>()
            .ToArray();
        if (selected.Length > 0)
        {
            PackageLinksSelected?.Invoke(this, new PackageLinksSelectedEventArgs(selected));
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!TryGetPackageUri(e.Uri, out var uri))
        {
            return;
        }

        e.Cancel = true;
        RaiseSelected(uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (TryGetPackageUri(e.Uri, out var uri))
        {
            e.Handled = true;
            RaiseSelected(uri);
            return;
        }

        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            Navigate(target);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!TryGetPackageUri(e.DownloadOperation.Uri, out var uri))
        {
            return;
        }

        e.Cancel = true;
        e.Handled = true;
        RaiseSelected(uri);
    }

    private void RaiseSelected(Uri uri)
    {
        if (ManifestUrl.TryGetManifestUri(uri, out var manifestUri))
        {
            uri = manifestUri;
        }
        else if (ManifestUrl.IsPs5ApplicationPackage(uri) &&
                 !uri.AbsolutePath.EndsWith("-DP.pkg", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "This link is a PS5 application PKG piece under /app/pkg/. The matching JSON " +
                "uses a different /app/info/ revision and hash, so it cannot be generated by " +
                "renaming the URL. Choose the JSON or SC link for this update instead.",
                "Select the manifest link",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        else if (ManifestUrl.IsNumberedPackagePiece(uri))
        {
            MessageBox.Show(
                this,
                "This is only one numbered PKG piece. Choose the JSON manifest so FetchPkg can " +
                "download and merge every piece.",
                "Select the manifest link",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        PackageLinksSelected?.Invoke(this, new PackageLinksSelectedEventArgs(new[] { uri }));
    }

    private static BrowserLink? CanonicalizeBrowserLink(BrowserLink link)
    {
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (ManifestUrl.TryGetManifestUri(uri, out var manifestUri))
        {
            var text = link.Text;
            if (!uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                text = string.IsNullOrWhiteSpace(text)
                    ? "JSON manifest"
                    : text + " [JSON manifest]";
            }

            return new BrowserLink(text, manifestUri.AbsoluteUri);
        }

        if (ManifestUrl.IsPs5ApplicationPackage(uri) &&
            !uri.AbsolutePath.EndsWith("-DP.pkg", StringComparison.OrdinalIgnoreCase))
        {
            // A PS5 /app/pkg/ URL does not contain enough information to reconstruct
            // the /app/info/ JSON path. Hide these pieces from the bulk picker.
            return null;
        }

        if (ManifestUrl.IsNumberedPackagePiece(uri))
        {
            return null;
        }

        return link;
    }

    private static bool TryGetPackageUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        uri = parsed;
        var host = uri.IdnHost;
        if (!host.Equals("playstation.net", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".playstation.net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void Navigate(Uri uri)
    {
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
    }
}
