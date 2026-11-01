@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem LLPlayer - manual download/update of yt-dlp and Deno.
rem Save this BAT in the LLPlayer root folder and run it to install/update.
rem Executables are installed in Plugins\YoutubeDL relative to this BAT.
rem Close LLPlayer before running: Windows cannot replace executables in use.

set "DEST=%~dp0Plugins\YoutubeDL"
set "WORK=%DEST%\.update-%RANDOM%-%RANDOM%"
echo.
echo LLPlayer - yt-dlp and Deno download/update
echo Destination: "%DEST%"
echo.

where curl.exe >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Windows curl.exe was not found.
    goto :failed
)

if not exist "%DEST%\" mkdir "%DEST%" 2>nul
if not exist "%DEST%\" (
    echo [ERROR] Could not create the destination folder.
    goto :failed
)

mkdir "%WORK%" 2>nul
if errorlevel 1 (
    echo [ERROR] Could not create the temporary folder.
    goto :failed
)

set "DENO_ARCHIVE=%WORK%\deno.zip"

echo [1/4] Downloading latest stable yt-dlp...
curl.exe --fail --location --retry 3 --retry-delay 2 --progress-bar ^
    --output "%WORK%\yt-dlp.exe" ^
    "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
if errorlevel 1 goto :failed

echo.
echo [2/4] Downloading latest stable Deno...
curl.exe --fail --location --retry 3 --retry-delay 2 --progress-bar ^
    --output "%DENO_ARCHIVE%" ^
    "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip"
if errorlevel 1 goto :failed

echo.
echo [3/4] Extracting and checking downloads...
powershell.exe -NoProfile -NonInteractive -Command ^
    "$ErrorActionPreference='Stop'; Expand-Archive -LiteralPath $env:DENO_ARCHIVE -DestinationPath $env:WORK -Force"
if errorlevel 1 goto :failed
if not exist "%WORK%\yt-dlp.exe" goto :failed
if not exist "%WORK%\deno.exe" goto :failed

"%WORK%\yt-dlp.exe" --version >nul 2>&1
if errorlevel 1 goto :failed
"%WORK%\deno.exe" --version >nul 2>&1
if errorlevel 1 goto :failed

echo.
echo [4/4] Replacing executables...
move /Y "%WORK%\yt-dlp.exe" "%DEST%\yt-dlp.exe" >nul
if errorlevel 1 goto :failed
move /Y "%WORK%\deno.exe" "%DEST%\deno.exe" >nul
if errorlevel 1 goto :failed

rmdir /S /Q "%WORK%" 2>nul
echo.
echo [OK] Installed versions:
echo yt-dlp:
"%DEST%\yt-dlp.exe" --version
echo.
echo Deno:
"%DEST%\deno.exe" --version
echo.
echo Both executables are ready in Plugins\YoutubeDL.
echo.
pause
exit /b 0

:failed
echo.
echo [ERROR] Download/update failed. Check network access and close LLPlayer.
echo Existing executables were not removed; a prior replacement may have succeeded.
if exist "%WORK%" rmdir /S /Q "%WORK%" 2>nul
echo.
pause
exit /b 1
