@echo off
rem CZN Translator control panel — double-click to open.
rem Starts the local server and opens the panel in the default browser.
cd /d "%~dp0"

set PY=..\.venv\Scripts\python.exe
if not exist "%PY%" set PY=python

rem Open the browser a moment after the server binds, then run the server in this window.
start "" cmd /c "timeout /t 2 >nul & start """" http://127.0.0.1:8777"
"%PY%" panel.py --db "..\czn.db" --port 8777

echo.
echo Panel stopped. Close this window.
pause
