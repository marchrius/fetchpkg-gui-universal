@echo off
setlocal
pushd "%~dp0"
dotnet publish src\FetchPkg.Win\FetchPkg.Win.csproj -c Release -r win-x64 --self-contained false -o publish\win-x64
set ERR=%errorlevel%
popd
exit /b %ERR%
