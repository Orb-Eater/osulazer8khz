@echo off
REM ============================================================
REM  osu!lazer 8k  --  UPDATE from GitHub
REM  Put this file in the folder that holds osu\ and osu-framework\.
REM  1. Stops if osu! is running or a repo has uncommitted changes.
REM  2. Backs up the current build and launchers to backups\.
REM  3. Pulls branch 8k-next (osu) and 8k-next-framework
REM     (osu-framework) from Orb-Eater/osulazer8khz, fast-forward only.
REM  4. Builds Release.
REM  5. Moves the old numbered test launchers (1-*.bat, 2-*.bat ...)
REM     to launchers-retired\, copies in the new ones from osu\8k\launchers.
REM  Nothing is deleted.
REM ============================================================
setlocal
cd /d "%~dp0"
set "REPO=https://github.com/Orb-Eater/osulazer8khz.git"

tasklist /FI "IMAGENAME eq osu!.exe" 2>nul | find /I "osu!.exe" >nul
if not errorlevel 1 (
    echo osu! is running. Close the game first, then run this again.
    goto :fail
)

if not exist "osu\.git" ( echo osu\ is not a git repo here. & goto :fail )
if not exist "osu-framework\.git" ( echo osu-framework\ is not a git repo here. & goto :fail )

call :checkrepo osu 8k-next || goto :fail
call :checkrepo osu-framework 8k-next-framework || goto :fail

for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%t"
set "BK=backups\before-update-%STAMP%"
echo Backing up the current build to %BK% ...
robocopy "osu\osu.Desktop\bin\Release\net10.0" "%BK%\net10.0" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo Backup failed. & goto :fail )
copy /Y *.bat "%BK%\" >nul
for /f %%h in ('git -C osu rev-parse HEAD') do echo osu %%h> "%BK%\HEADS.txt"
for /f %%h in ('git -C osu-framework rev-parse HEAD') do echo osu-framework %%h>> "%BK%\HEADS.txt"

echo.
echo Pulling osu (8k-next) ...
git -C osu fetch "%REPO%" 8k-next || goto :fail
git -C osu merge --ff-only FETCH_HEAD || ( echo osu: cannot fast-forward. Nothing was changed. & goto :fail )
echo Pulling osu-framework (8k-next-framework) ...
git -C osu-framework fetch "%REPO%" 8k-next-framework || goto :fail
git -C osu-framework merge --ff-only FETCH_HEAD || ( echo osu-framework: cannot fast-forward. & goto :fail )

echo.
echo Building Release ...
dotnet build osu\osu.Desktop -c Release -v q -nologo || ( echo BUILD FAILED. The previous build is in %BK%\net10.0 & goto :fail )

echo.
dir /b "osu\8k\launchers\*.bat" 2>nul | findstr /R "^[0-9][0-9]*-" >nul
if not errorlevel 1 (
    if not exist "launchers-retired\%STAMP%" mkdir "launchers-retired\%STAMP%"
    for /f "delims=" %%f in ('dir /b *.bat 2^>nul ^| findstr /R "^[0-9][0-9]*-"') do move /Y "%%f" "launchers-retired\%STAMP%\" >nul
    for /f "delims=" %%f in ('dir /b "osu\8k\launchers\*.bat" ^| findstr /R "^[0-9][0-9]*-"') do (
        copy /Y "osu\8k\launchers\%%f" . >nul
        echo New test launcher: %%f
    )
) else (
    echo No numbered test launchers in this update.
)

echo.
echo Done. osu:
git -C osu log -1 --format="  %%h %%s"
echo osu-framework:
git -C osu-framework log -1 --format="  %%h %%s"
echo Read osu\8k\NEXT.md for what to test.
pause
exit /b 0

:checkrepo
set "B="
for /f %%b in ('git -C %1 branch --show-current') do set "B=%%b"
if /I not "%B%"=="%2" (
    echo %1 is on branch "%B%", expected "%2". Switch with:  git -C %1 switch %2
    exit /b 1
)
git -C %1 diff --quiet || ( echo %1 has uncommitted changes. Commit or stash them first. & exit /b 1 )
git -C %1 diff --cached --quiet || ( echo %1 has staged changes. Commit them first. & exit /b 1 )
exit /b 0

:fail
echo.
echo Update stopped. Nothing after this point was changed.
pause
exit /b 1
