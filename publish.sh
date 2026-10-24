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
  --output "$OUTPUT"
if [[ "$RID" == osx-* ]]; then
  cp "$ROOT/packaging/Info.plist" "$APP/Contents/Info.plist"
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
