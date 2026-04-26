@echo off
cd /d %~dp0
dotnet publish MCPVault\MCPVault.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -o publish

echo.
echo Š®—¹: publish\ ‚É MCPVault.exe ‚ªo—Í‚³‚ê‚Ü‚µ‚½
pause