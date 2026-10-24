# FetchPkg — macOS, Linux and Windows

Desktop downloader for PlayStation package manifests and standalone PKG files,
based on the workflow of [fetchpkg](https://github.com/ps5-payload-dev/fetchpkg).
The new **Avalonia UI** application reuses the existing .NET download engine and
targets Apple Silicon / Intel Macs, Linux x64 / ARM64 and Windows.

## Migration status

The cross-platform implementation, packaging scripts and CI workflow are included.
**It has not yet been compiled or launched in the migration environment:** that
environment has no .NET SDK and cannot download one. A successful CI build and
manual launch on each target are still required before calling this a tested
release. No compiled application is included in this source folder.

The original Windows Forms / WebView2 application remains available separately in
`FetchPkg.Windows.sln`; see [the Windows documentation](docs/WINDOWS.md).
`FetchPkg.sln` contains only the cross-platform app, core and smoke tests, so it
does not require Windows targeting or WebView2 on macOS/Linux.

## Requirements

- To build: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or a newer
  SDK capable of targeting .NET 8, and access to NuGet for the first restore.
- macOS: Apple Silicon or Intel. The bundle declares macOS 12 as its minimum;
  compatibility with individual OS versions still needs runtime validation.
- Linux: a graphical X11 session (or XWayland), glibc and Avalonia's native graphics
  dependencies. Typical Debian/Ubuntu packages are `libx11-6 libice6 libsm6
  libfontconfig1 libfreetype6 libxext6 libxrandr2 libxi6 libxcursor1 libgl1`.
  Minimal installations may require additional graphics/system libraries.
- Windows: Windows 10/11 x64 for the desktop build.

Published builds are self-contained and do not require .NET to be installed on
the destination computer. Linux builds here target glibc distributions, not musl
or Alpine. The UI uses Avalonia 11.3.10, with trimming and single-file publishing
disabled to preserve bindings and native dependencies.

## Build and run

From this directory, on macOS or Linux:

```sh
./build.sh
./run.sh
```

On any platform (including Windows):

```sh
dotnet build FetchPkg.sln -c Release
dotnet run --project src/FetchPkg.Desktop -c Release
```

## Create an application

```sh
./publish.sh osx-arm64     # Apple Silicon
./publish.sh osx-x64       # Intel Mac
./publish.sh linux-x64
./publish.sh linux-arm64
```

Omit the argument to detect the current macOS/Linux architecture. On a Mac:

```sh
./publish.sh
open publish/osx-arm64/FetchPkg.app
```

For Intel, use the `osx-x64` folder. Copy the entire `.app` to Applications if
desired. Keep all files inside the bundle together. Publishing on macOS adds an
ad-hoc signature for local use; public distribution still needs a Developer ID
signature and notarization, which this script does not perform.

On Linux, run `publish/linux-x64/FetchPkg` (or the ARM64 equivalent). The included
`.desktop` file expects `FetchPkg` on `PATH`; adjust its `Exec` to the absolute
installed executable path before installing the launcher.

For the new Windows interface:

```powershell
dotnet publish src/FetchPkg.Desktop -c Release -r win-x64 --self-contained true -o publish/desktop-win-x64
```

The existing `build.cmd` / `publish-win-x64.cmd` still build the legacy Windows
application. `publish.sh win-x64` can publish the Avalonia version from Bash.
Use a fresh publish destination for release packaging when changing dependencies.

## Usage

1. Choose the output folder. Each queue entry keeps the destination selected when
   it was added, even if the output setting subsequently changes.
2. Paste a Sony JSON manifest, a PS5 `_sc.pkg` link, or a standalone PKG URL.
   Multiple links can be added together, one per line.
3. Click **Add links**, or copy links and click **Paste links**.
4. Select entries and click **Start / retry selected**. With no selection, Start
   processes all unfinished entries sequentially. Each package uses the configured
   number of parallel HTTP connections.
5. Pause, resume, cancel or retry entries as needed. Cancelling preserves partial
   data and the `.fetchpkg.state.json` sidecar. Removing a queue entry does not
   delete its files. To resume after restarting the app, add the same URL and
   select the same output folder; the queue itself is not persisted.

Completed entries are not restarted. Existing files get a numbered destination
unless their sidecar identifies the same source. The engine then checks the full
manifest fingerprint and logical file size before accepting completed ranges.
Closing stops the pending queue and waits for active downloads to cancel and
finish saving state.

The default is eight connections and 16 MiB segments. Proxy configuration,
SHA-1/SHA-256 piece verification and automatic retry of corrupt pieces remain
available. TLS verification is enabled by default. The optional **Ignore SSL
errors (unsafe)** setting applies to newly started downloads only.

## PROSPEROPatches and platform differences

**PROSPEROPatches ↗** opens the actual site in the default browser. Search there,
copy the JSON or SC link with the browser's **Copy link address** command, and
return to FetchPkg to paste it. No private site API is used.

The new app does **not** embed WebView2, intercept browser navigation, scan pages
for links or change browser certificate handling. Browser proxy/TLS settings
remain controlled by the browser. Embedded browsing and its page scanner remain
available only in the legacy Windows app. The new input supports plain-text links;
Windows `.url` drag-and-drop import is not implemented in this version.

PS5 `_sc.pkg` conversion preserves the entire `/app/info/<revision>/f_<hash>/`
directory and query string. Numbered `/app/pkg/` pieces require their actual JSON
manifest; the application never guesses a manifest by rewriting their path.
Legacy PS4 `ppkgo` sibling conversions are retained.

## Sparse files and resume

On Windows, sparse output is enabled using `FSCTL_SET_SPARSE`. On macOS/Linux,
extending a regular file with `SetLength` uses the operating system's native hole
semantics; no Windows DLL is invoked. APFS and filesystems such as ext4 support
unwritten holes. The sparse indicator on Unix means that this strategy was
selected, not that the volume's physical allocation has been measured. FAT,
network shares and other volumes may allocate differently. The checkbox controls
the explicit sparse strategy; Unix filesystems can create holes even when it is off.

Logical file size can therefore exceed allocated disk space during a download.
Use `du -h <file>` and `ls -lh <file>` on Unix to compare allocation and logical
size. SHA checks, range state and atomic state replacement use the same engine
and sidecar format across all frontends.

Settings are stored below `Environment.SpecialFolder.LocalApplicationData` in
`FetchPkg/settings.json`. The legacy Windows app uses its separate settings file.

## Verification

```sh
dotnet run --project tests/FetchPkg.SmokeTests -c Release
```

The dependency-free smoke runner uses deterministic HTTP fixtures and real local
files. It checks parallel ranges, cancellation/resume, skipping completed ranges,
SHA-1/SHA-256 verification, corrupt-piece retry, sparse gaps, invalid Content-Range
rejection, cancellation while paused and PS5 URL normalization. It exits nonzero
on failure and does not download from Sony.

`.github/workflows/desktop.yml` builds and runs these checks on macOS, Linux and
Windows, then publishes application artifacts for both Mac/Linux architectures
and Windows x64. These are local build/test definitions until the project is
placed in a GitHub repository and the workflow runs. UI interaction, Finder
launch, clipboard integration and actual CDN downloads need manual validation.

## License

GPL-3.0-or-later. Avalonia and the legacy WebView2 dependency have their own
licenses. PROSPEROPatches is an external website and is not bundled.
The application downloads user-selected public URLs; it does not authenticate to
PSN, decrypt packages, bypass licenses or install content.
