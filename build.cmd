@echo off
rem 构建 GamerTool 管理器（需 .NET 9 SDK）
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -o publish
if %errorlevel% neq 0 (
  echo 构建失败
  exit /b 1
)
copy /y publish\GamerToolManager.exe ..\GamerToolManager.exe >nul
echo.
echo 构建完成: %~dp0publish\GamerToolManager.exe
echo 同时已复制到: %~dp0..\GamerToolManager.exe
pause
