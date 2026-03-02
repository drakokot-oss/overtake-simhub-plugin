@echo off
setlocal
title Overtake Telemetry - Instalador

echo.
echo ==========================================
echo   Overtake Telemetry - Plugin Installer
echo ==========================================
echo.

set "SIMHUB_DIR="
for %%D in (
    "%ProgramFiles(x86)%\SimHub"
    "%ProgramFiles%\SimHub"
    "D:\Program Files (x86)\SimHub"
    "D:\Program Files\SimHub"
    "D:\SimHub"
    "C:\SimHub"
) do (
    if exist "%%~D\SimHubWPF.exe" (
        set "SIMHUB_DIR=%%~D"
        goto :found
    )
)

echo [ERRO] SimHub nao encontrado automaticamente.
echo.
set /p "SIMHUB_DIR=Digite o caminho do SimHub (ex: C:\Program Files (x86)\SimHub): "
if not exist "%SIMHUB_DIR%\SimHubWPF.exe" (
    echo [ERRO] SimHubWPF.exe nao encontrado em "%SIMHUB_DIR%"
    pause
    exit /b 1
)

:found
echo SimHub encontrado em: %SIMHUB_DIR%
echo.

REM Check if SimHub is running
tasklist /FI "IMAGENAME eq SimHubWPF.exe" 2>nul | find /I "SimHubWPF.exe" >nul
if %errorlevel%==0 (
    echo [AVISO] SimHub esta rodando. Feche o SimHub antes de continuar.
    echo.
    pause
    tasklist /FI "IMAGENAME eq SimHubWPF.exe" 2>nul | find /I "SimHubWPF.exe" >nul
    if %errorlevel%==0 (
        echo [ERRO] SimHub ainda esta rodando. Feche-o e tente novamente.
        pause
        exit /b 1
    )
)

echo Copiando plugin...
copy /Y "%~dp0Overtake.SimHub.Plugin.dll" "%SIMHUB_DIR%\Overtake.SimHub.Plugin.dll"
if %errorlevel%==0 (
    echo.
    echo ==========================================
    echo   Instalacao concluida com sucesso!
    echo ==========================================
    echo.
    echo Proximo passo: Abra o SimHub e ative o plugin "Overtake F1 25 Telemetry"
) else (
    echo.
    echo [ERRO] Falha ao copiar o plugin. Execute como Administrador.
)

echo.
pause
