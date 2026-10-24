#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -s)/$(uname -m)" in
    Darwin/arm64) RID=osx-arm64 ;;
    Darwin/x86_64) RID=osx-x64 ;;
    Linux/aarch64|Linux/arm64) RID=linux-arm64 ;;
    Linux/x86_64) RID=linux-x64 ;;
    *) echo "Specify a target: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64, win-arm64" >&2; exit 2 ;;
  esac
fi
case "$RID" in
  osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64|win-arm64) ;;
  *) echo "Unsupported target: $RID" >&2; exit 2 ;;
esac
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 8 SDK (or newer) is required: https://dotnet.microsoft.com/download" >&2
  exit 127
fi
VERSION_ARGS=()
if [[ -n "${FETCHPKG_VERSION:-}" ]]; then
  if [[ ! "$FETCHPKG_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
    echo "Invalid release version: $FETCHPKG_VERSION. Expected 1.2.3 or 1.2.3-rc.1." >&2
    exit 2
  fi
  BASE_VERSION="${FETCHPKG_VERSION%%-*}"
  VERSION_ARGS=("-p:Version=$FETCHPKG_VERSION" "-p:InformationalVersion=$FETCHPKG_VERSION"
    "-p:AssemblyVersion=$BASE_VERSION.0" "-p:FileVersion=$BASE_VERSION.0")
fi
DEST="$ROOT/publish/$RID"
if [[ "$RID" == osx-* ]]; then
  APP="$DEST/FetchPkg.app"
  OUTPUT="$APP/Contents/MacOS"
  mkdir -p "$OUTPUT" "$APP/Contents/Resources"
else
  OUTPUT="$DEST"
  mkdir -p "$OUTPUT"
fi
dotnet publish "$ROOT/src/FetchPkg.Desktop/FetchPkg.Desktop.csproj" \
  --configuration Release --runtime "$RID" --self-contained true \
  -p:UseAppHost=true -p:PublishTrimmed=false -p:PublishSingleFile=false \
  --output "$OUTPUT" ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"}
cp "$ROOT/LICENSE" "$DEST/LICENSE"
if [[ "$RID" == osx-* ]]; then
  if [[ -n "${FETCHPKG_VERSION:-}" ]]; then
    # Apple's bundle version fields use the numeric version, without prerelease labels.
    sed -e "s|<key>CFBundleShortVersionString</key><string>[^<]*</string>|<key>CFBundleShortVersionString</key><string>$BASE_VERSION</string>|" \
        -e "s|<key>CFBundleVersion</key><string>[^<]*</string>|<key>CFBundleVersion</key><string>$BASE_VERSION</string>|" \
        "$ROOT/packaging/Info.plist" > "$APP/Contents/Info.plist"
  else
    cp "$ROOT/packaging/Info.plist" "$APP/Contents/Info.plist"
  fi
  chmod +x "$OUTPUT/FetchPkg"
  # Ad-hoc signing is for local use. Developer ID signing and notarization
  # must be performed separately before distributing a release to others.
  if [[ "$(uname -s)" == Darwin ]]; then
    codesign --force --deep --sign - "$APP"
  fi
  echo "Application bundle: $APP"
elif [[ "$RID" == linux-* ]]; then
  chmod +x "$OUTPUT/FetchPkg"
  cp "$ROOT/packaging/FetchPkg.desktop" "$DEST/FetchPkg.desktop"
  echo "Run: $OUTPUT/FetchPkg"
else
  echo "Windows application: $OUTPUT/FetchPkg.exe"
fi
