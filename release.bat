@echo off
setlocal enabledelayedexpansion

REM ============================================================
REM  SaveGuard release helper
REM
REM  Usage:    release.bat v1.0.0
REM
REM  Creates and pushes a version tag, which triggers the GitHub
REM  Actions release workflow (build -> Velopack pack -> publish a
REM  GitHub Release that installed apps auto-update to).
REM
REM  Foolproofing: if the tag (or its GitHub Release) already
REM  exists, it is deleted first, so re-running the same version
REM  is safe.
REM ============================================================

set "TAG=%~1"
if "%TAG%"=="" goto :usage

REM --- sanity: looks like vX.Y.Z (pre-release suffix allowed) ---
echo %TAG%|findstr /R "^v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul
if errorlevel 1 (
    echo [warn] "%TAG%" does not look like a version tag e.g. v1.0.0
    set /p "OK=    Continue anyway? (y/N): "
    if /I not "!OK!"=="y" goto :abort
)

REM --- must be in the repo ---
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [x] Not inside a git repository. Run this from the repo root.
    exit /b 1
)

for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"
echo.
echo   Tag    : %TAG%
echo   Branch : !BRANCH!
echo   Commit :
git --no-pager log --oneline -1
if /I not "!BRANCH!"=="main" echo   [warn] You are not on "main" - releases usually come from main.
echo.

set /p "GO=Release %TAG% now? Any existing tag/release will be replaced. (y/N): "
if /I not "!GO!"=="y" goto :abort

echo.
echo   Removing any existing %TAG% (local tag, remote tag, release)...
git tag -d %TAG% >nul 2>&1
git push origin :refs/tags/%TAG% >nul 2>&1
gh release delete %TAG% --yes >nul 2>&1

echo   Creating and pushing %TAG%...
git tag %TAG%
if errorlevel 1 (
    echo [x] Failed to create tag.
    exit /b 1
)
git push origin %TAG%
if errorlevel 1 (
    echo [x] Failed to push tag.
    exit /b 1
)

echo.
echo   Done. GitHub Actions is now building the release:
echo     https://github.com/HouseAlwaysWin/SaveGuard/actions
echo.
echo   Watch it live with:  gh run watch
goto :end

:usage
echo Usage:   release.bat ^<version-tag^>
echo Example: release.bat v1.0.0
exit /b 1

:abort
echo Aborted. Nothing changed.
exit /b 1

:end
endlocal
