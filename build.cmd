@echo off
setlocal
pushd "%~dp0"
dotnet restore FetchPkg.Windows.sln
if errorlevel 1 exit /b %errorlevel%
dotnet build FetchPkg.Windows.sln -c Release --no-restore
set ERR=%errorlevel%
popd
exit /b %ERR%
