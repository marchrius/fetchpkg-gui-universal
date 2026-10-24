# FetchPkg for Windows

A Windows GUI reimplementation of the download workflow used by
[`ps5-payload-dev/fetchpkg`](https://github.com/ps5-payload-dev/fetchpkg).
It downloads official PlayStation package data referenced by Sony JSON manifests,
merges manifest pieces directly into the final PKG, and also supports direct
single-PKG URLs when no JSON manifest exists.

## Implemented features

- Native Windows Forms interface for Visual Studio 2022 and .NET 8.
- Package queue with pause, resume, cancel, retry status, speed and ETA.
- Parallel HTTP range downloading inside large manifest pieces.
- Parallel downloading of independent manifest pieces.
- Configurable connection count and segment size.
- SHA-1 and SHA-256 verification of manifest pieces.
- Automatic re-download of only the failed piece after a hash mismatch.
- Crash/cancel-safe resume state in `<file>.fetchpkg.state.json`.
- NTFS sparse output through `FSCTL_SET_SPARSE`; setting the final logical length
  does not reserve the whole PKG on disk in advance.
- Proxy support through `SocketsHttpHandler`.
- Optional **Ignore SSL errors (unsafe)** mode for both package HTTP requests and
  the embedded PROSPEROPatches WebView2 browser. It is disabled by default.
- Embedded PROSPEROPatches browser using Microsoft Edge WebView2:
  - direct lookup by `PPSAxxxxx`;
  - search by game name using the site's own current interface;
  - selection of game updates and additional content;
  - interception of selected official Sony `.json` or `.pkg` links;
  - a page scanner for collecting multiple visible download links.
- Exact PS5 `_sc.pkg` → `.json` conversion while preserving the complete
  `/app/info/<revision>/f_<hash>/` path.
- PS5 `/app/pkg/` links are never guessed into JSON URLs: their manifest can use
  a different revision and hash. The UI asks for the JSON or `_sc.pkg` link instead.
- Legacy PS4 sibling-manifest conversion remains available for `ppkgo` links.

## Why WebView2 is used for PROSPEROPatches

PROSPEROPatches does not publish a stable public API contract. Calling a private
endpoint would make the application break whenever the site changes its frontend.
The embedded WebView2 control runs the actual site, so its search, region switch,
update list and additional-content list remain authoritative. The application only
intercepts the final public PlayStation download link chosen by the user.

## Requirements

- Windows 10 or Windows 11 x64.
- Visual Studio 2022 with the **.NET desktop development** workload, or the .NET 8 SDK.
- Microsoft Edge WebView2 Runtime. It is normally already present on supported Windows versions.
- NTFS is recommended. Sparse mode is attempted on other filesystems but can be unavailable.

## Build in Visual Studio

1. Open `FetchPkg.Windows.sln`.
2. Allow NuGet to restore `Microsoft.Web.WebView2`.
3. Select `Release` and `Any CPU` or `x64`.
4. Build and run `FetchPkg.Win`.

Command-line build:

```bat
build.cmd
```

Publish a framework-dependent x64 build:

```bat
publish-win-x64.cmd
```

The output is written to `publish\win-x64`.

## Usage

1. Choose an output directory.
2. Either paste a Sony manifest/PKG URL and click **Add**, or open
   **PROSPEROPatches…**.
3. In the embedded site, search by a title ID or game name and open the desired
   update or additional-content entry.
4. Choose the JSON or SC link for a PS5 update. SC links are converted to the
   exact sibling JSON URL without changing the revision or hash path. Raw PS5
   `/app/pkg/` pieces are rejected because their JSON path cannot be inferred.
   **Find download links** can collect several usable links visible on the page.
5. Enable **Ignore SSL errors (unsafe)** only when a proxy, TLS inspection appliance,
   or server with an invalid certificate prevents access. This disables certificate
   validation for newly started downloads and for newly opened PROSPEROPatches windows.
6. Select queue entries and click **Start selected**. Existing completed files are
   not overwritten automatically; a numbered output name is chosen unless a matching
   resume-state file is present.

The default is eight concurrent requests with 16 MiB ranges. Increasing the
connection count can reduce performance or trigger CDN throttling. Values between
4 and 12 are usually a sensible starting point.

## PS5 manifest URL handling

For PS5 title updates, the manifest has this form:

```text
https://sgst.prod.dl.playstation.net/sgst/prod/00/<TITLE_ID>/app/info/<REVISION>/f_<HASH>/<CONTENT_ID>.json
```

The matching `_sc.pkg` is in the same directory, so replacing only `_sc.pkg` with
`.json` is safe. Application package pieces are under `/app/pkg/` and can use a
different revision and hash; changing `_0.pkg` or `-DP.pkg` to `.json` in that path
produces an invalid URL and is no longer attempted. HTTP failures now include the
exact request URL in the queue details.

## Sparse-file behavior

The program first marks the destination handle as sparse, then sets its logical
length to `originalFileSize`. NTFS allocates physical clusters only for ranges that
are actually written. This avoids the long full-file zero-fill/preallocation step
and permits out-of-order range writes.

Explorer still displays the final logical file size. To inspect physical allocation,
use PowerShell or `fsutil sparse queryflag <file>` and compare `Size` with
`Size on disk` in file properties.

## Resume format

After each completed range, the program atomically updates:

```text
<output>.fetchpkg.state.json
```

The state is accepted only when its schema version, manifest URL, final logical
size, output-initialization marker, and SHA-256 fingerprint of every manifest piece
match. It is deleted only after the output has been flushed and all configured hash
checks succeed. Cancelling the application preserves both the partial sparse PKG
and this state file.

## Notes

- TLS certificate verification is enabled by default. The optional **Ignore SSL
  errors (unsafe)** checkbox accepts expired, self-signed, hostname-mismatched, and
  otherwise untrusted certificates. Use it only on networks and servers you trust.
- Only public URLs selected by the user are downloaded. The program does not
  authenticate to PlayStation Network, decrypt packages, bypass licenses, or
  install content.
- The PROSPEROPatches site and Sony CDN can change independently of this project.
  The WebView2 integration is deliberately isolated in `ProsperoBrowserForm.cs`.

## License

GPL-3.0-or-later, matching the upstream fetchpkg licensing model. WebView2 is a
Microsoft NuGet dependency under its own license. PROSPEROPatches is not bundled
and is only displayed as an external website.
