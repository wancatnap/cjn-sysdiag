@echo off
REM ============================================================
REM  Save current changes into local git history.
REM  Double-click to run. Nothing is uploaded - local repo only.
REM ============================================================
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo ===== what changed =====
git status --short
echo.

set "msg="
set /p "msg=Describe this change [Enter=update]: "
if "%msg%"=="" set "msg=update"

git add -A
git -c user.name=wancatnap -c user.email=wancatnap@users.noreply.github.com commit -m "%msg%"

echo.
echo ===== last 5 commits =====
git log --oneline -5
echo.
echo Undo last commit but keep files: git reset --soft HEAD~1
echo.
pause
