#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 8 SDK (or newer) is required: https://dotnet.microsoft.com/download" >&2
  exit 127
fi
dotnet run --project "$ROOT/src/FetchPkg.Desktop/FetchPkg.Desktop.csproj" --configuration Release -- "$@"
