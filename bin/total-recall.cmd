@echo off
SETLOCAL EnableDelayedExpansion
:: total-recall MCP server launcher for Windows
:: Requires Bun — dist\index.js uses bun:sqlite, which node cannot resolve.
:: Prefers bundled Bun (installed by scripts\postinstall.js), falls back to system Bun.

SET BUN_VERSION=1.2.10
SET BUNDLED_BUN=%USERPROFILE%\.total-recall\bun\%BUN_VERSION%\bun.exe

SET SCRIPT_DIR=%~dp0
SET ENTRY=%SCRIPT_DIR%..\dist\index.js

IF NOT EXIST "%ENTRY%" (
  echo total-recall: error: could not find dist\index.js. 1>&2
  echo   Run 'npm run build' or 'npm install -g @strvmarv/total-recall'. 1>&2
  exit /b 1
)

:: Priority 1: bundled Bun
IF EXIST "%BUNDLED_BUN%" (
  "%BUNDLED_BUN%" "%ENTRY%" %*
  exit /b %ERRORLEVEL%
)

:: Priority 2: system Bun (warn — version may not match)
WHERE bun >nul 2>&1
IF %ERRORLEVEL% EQU 0 (
  echo total-recall: warning: bundled bun v%BUN_VERSION% not found, using system bun. Version mismatch possible. 1>&2
  echo   Re-run 'npm install' to download bun v%BUN_VERSION%. 1>&2
  bun "%ENTRY%" %*
  exit /b !ERRORLEVEL!
)

echo total-recall: error: bun runtime not found. 1>&2
echo   Expected bundled bun at %%USERPROFILE%%\.total-recall\bun\%BUN_VERSION%\bun.exe (installed by 'npm install'). 1>&2
echo   Fix: run 'npm install' inside the plugin directory, or install bun manually (https://bun.sh/install). 1>&2
exit /b 1
