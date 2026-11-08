@echo off
REM ============================================================
REM  Re-sign the remote notice after editing notice-source.json
REM  and open the folder + repo page for uploading.
REM
REM  Why this exists: the signature covers title / message / url /
REM  urlText / level / enabled / expires. If you edit the text in
REM  notice.json directly, the signature no longer matches and the
REM  client rejects it as tampered.
REM ============================================================
chcp 65001 >nul
cd /d "%~dp0"

REM Locate the release exe without hardcoding the Chinese filename
set "APP="
for %%F in ("%~dp0*.exe") do set "APP=%%~fF"

if not defined APP (
    echo [FAILED] release exe not found in this folder.
    pause
    exit /b 1
)

echo.
echo ==== re-signing notice-source.json ====
echo.

"%APP%" --sign-notice notice-source.json notice.json

if errorlevel 1 (
    echo.
    echo [FAILED] signing failed. Check keys\notice-private.pem exists.
    pause
    exit /b 1
)

echo.
echo Opening folder and repo page...
explorer /select,"%~dp0notice.json"
start "" https://github.com/wancatnap/sysdiag-notice

echo.
echo Next step: on the repo page, open notice.json, click the pencil icon,
echo replace the whole content with the new notice.json, then commit.
echo.
echo Then verify with:  --notice-probe
echo Expect:  source=remote  verified=OK  showable=True
echo.
pause
