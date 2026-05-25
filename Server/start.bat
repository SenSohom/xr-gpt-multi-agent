@echo off
REM ============================================================
REM VR-XR HUD Python server startup script (Windows)
REM First-time setup:
REM   1) python -m venv .venv
REM   2) .venv\Scripts\activate
REM   3) pip install torch --index-url https://download.pytorch.org/whl/cu121
REM      (or use cpu URL if no CUDA)
REM   4) pip install -r requirements.txt
REM ============================================================

setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo [start.bat] No virtualenv found at .venv\
    echo Run setup first:
    echo   python -m venv .venv
    echo   .venv\Scripts\activate
    echo   pip install torch --index-url https://download.pytorch.org/whl/cu121
    echo   pip install -r requirements.txt
    pause
    exit /b 1
)

call ".venv\Scripts\activate.bat"

REM Pin HuggingFace cache to a fixed folder INSIDE the Server directory so model
REM weights are downloaded ONCE and reused forever - no matter
REM what %USERPROFILE% looks like or which terminal launches this script.
set "HF_HOME=%~dp0.hf_cache"
set "HF_HUB_CACHE=%~dp0.hf_cache\hub"
set "TRANSFORMERS_CACHE=%~dp0.hf_cache\transformers"
set "HF_HUB_DISABLE_SYMLINKS_WARNING=1"
REM Skip re-checking the hub for newer revisions on every launch (offline-after-first-download).
set "HF_HUB_OFFLINE=0"
set "TRANSFORMERS_OFFLINE=0"
if not exist "%HF_HOME%" mkdir "%HF_HOME%"

REM Preload BOTH YOLO and VLM. If we don't preload, the first /vlm/ask call can
REM hang for minutes on first download
REM while transformers downloads weights, which makes the Quest UI look frozen.
python server.py --host 0.0.0.0 --port 8766 --preload-yolo --preload-vlm %*

REM Pause on crash so the user can read the traceback instead of the window vanishing.
if errorlevel 1 (
    echo.
    echo [start.bat] server.py exited with error code %errorlevel%.
    echo Check the traceback above. Common causes:
    echo   - transformers got upgraded to 5.x ^(reinstall: pip install "transformers>=4.44,<5.0"^)
    echo   - port 8766 already in use ^(close other server instance^)
    echo   - GPU OOM ^(close other CUDA processes^)
    pause
)

endlocal
