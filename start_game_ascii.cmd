@echo off
setlocal

echo ========================================
echo Era_AI Launcher ASCII
echo ========================================
echo.

set "GAME_DIR=%~dp0"
cd /d "%GAME_DIR%"

echo GAME_DIR=%CD%
echo.

set "PYTHON_EXE=D:\Anaconda\envs\env\python.exe"
if not exist "%PYTHON_EXE%" set "PYTHON_EXE=python"

echo PYTHON_EXE=%PYTHON_EXE%
echo.

if not exist "AI\select_model.py" (
    echo ERROR: AI\select_model.py not found.
    echo Current folder: %CD%
    pause
    exit /b 1
)

if not exist "AI\ai_bridge.py" (
    echo ERROR: AI\ai_bridge.py not found.
    echo Current folder: %CD%
    pause
    exit /b 1
)

if not exist "AI\model_profiles.ini" (
    echo WARNING: AI\model_profiles.ini not found.
    echo select_model.py may create a default profile file.
    echo.
)

echo Step 1 of 3: select model profile
echo ----------------------------------------
"%PYTHON_EXE%" "AI\select_model.py"
set "RET=%ERRORLEVEL%"
echo.
echo select_model exit code: %RET%
echo.

if "%RET%"=="2" (
    echo Startup canceled.
    pause
    exit /b 0
)

if not "%RET%"=="0" (
    echo ERROR: model selection failed.
    pause
    exit /b 1
)

if not exist "AI\AI_CONFIG.txt" (
    echo ERROR: AI\AI_CONFIG.txt was not generated.
    pause
    exit /b 1
)

echo Step 2 of 3: start AI Bridge
echo ----------------------------------------
start "AI Bridge" cmd /k ""%PYTHON_EXE%" "%CD%\AI\ai_bridge.py""
timeout /t 2 /nobreak >nul

echo Step 3 of 3: find Emuera
echo ----------------------------------------
set "EMUERA_EXE="
for %%F in ("%CD%\Emuera*.exe") do (
    if exist "%%~fF" (
        set "EMUERA_EXE=%%~fF"
        goto FOUND_EMUERA
    )
)

:FOUND_EMUERA
if "%EMUERA_EXE%"=="" (
    echo ERROR: Emuera executable not found.
    echo Current folder: %CD%
    pause
    exit /b 1
)

echo EMUERA_EXE=%EMUERA_EXE%
start "" "%EMUERA_EXE%"

echo.
echo Startup completed.
echo Keep this window for checking logs.
pause
exit /b 0
