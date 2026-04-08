@echo off
chcp 65001 >nul
pause
title DouyinBarrageGrab Build Script

echo ===============================================
echo   DouyinBarrageGrab - Windows Build Script
echo ===============================================
echo.

::: Check Windows
echo [Check] OS...
if not "%OS%"=="Windows_NT" (
    echo [Error] This script only runs on Windows!
    pause
    exit /b 1
)

::: Get directories
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%BarrageGrab"
set "SOLUTION_DIR=%SCRIPT_DIR%"

::: Output settings
set "OUTPUT_DIR=%SCRIPT_DIR%Output"
set "EXE_NAME=WssBarrageServer.exe"

echo [Info] Project: %PROJECT_DIR%
echo [Info] Output: %OUTPUT_DIR%
echo.

::: Create output directory
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

::: Check .NET Framework
echo [Check] .NET Framework 4.6.2...
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [Warn] .NET Framework 4.6.2+ not found
    echo [Info] Download: https://dotnet.microsoft.com/download/dotnet-framework/net462
)
echo.

::: Build mode selection
echo Select build mode:

echo   [1] Debug
echo   [2] Release (default)
echo.

set /p MODE_CHOICE="Enter choice (1/2, default 2): "
if "%MODE_CHOICE%"=="1" (
    set "BUILD_CONFIG=Debug"
) else (
    set "BUILD_CONFIG=Release"
)

echo.
echo [Info] Build mode: %BUILD_CONFIG%
echo.

::: Clean old build
echo [Step 1] Cleaning old build...
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%" (
    rd /s /q "%PROJECT_DIR%\bin\%BUILD_CONFIG%"
    echo   Cleaned bin/%BUILD_CONFIG%
)
if exist "%OUTPUT_DIR%\%EXE_NAME%" (
    del /q "%OUTPUT_DIR%\%EXE_NAME%"
    echo   Cleaned old output
)
echo.

::: Restore NuGet packages
echo [Step 2] Restoring NuGet packages...
if exist "%SOLUTION_DIR%BarrageService.sln" (
    where dotnet >nul 2>&1
    if %ERRORLEVEL% equ 0 (
        echo [Info] Using dotnet restore...
        dotnet restore "%SOLUTION_DIR%BarrageService.sln"
        if %ERRORLEVEL% neq 0 (
            echo [Warn] dotnet restore failed, trying nuget...
            nuget restore "%SOLUTION_DIR%BarrageService.sln"
        )
    ) else (
        where nuget >nul 2>&1
        if %ERRORLEVEL% equ 0 (
            echo [Info] Using nuget restore...
            nuget restore "%SOLUTION_DIR%BarrageService.sln"
        ) else (
            echo [Warn] NuGet not found, skipping restore
        )
    )

) else (
    echo [Error] BarrageService.sln not found!
    pause
    exit /b 1
)
echo.

::: Build project
echo [Step 3] Building project (Configuration=%BUILD_CONFIG%)...
echo.

:::: Try Rider MSBuild first
if exist "C:\Program Files\JetBrains\JetBrains Rider 2024.1\tools\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBUILD_PATH=C:\Program Files\JetBrains\JetBrains Rider 2024.1\tools\MSBuild\Current\Bin\MSBuild.exe"
)
if not defined MSBUILD_PATH (
    for /d %%d in ("C:\Program Files\JetBrains\JetBrains Rider*") do (
        if exist "%%d\tools\MSBuild\Current\Bin\MSBuild.exe" (
            set "MSBUILD_PATH=%%d\tools\MSBuild\Current\Bin\MSBuild.exe"
        )
    )
)

if defined MSBUILD_PATH (
    echo [Info] Using Rider MSBuild: %MSBUILD_PATH%
    "%MSBUILD_PATH%" "%SOLUTION_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal
) else (
    :::: Fallback to dotnet msbuild
    echo [Warn] .NET Framework MSBuild not found, using dotnet msbuild...
    dotnet msbuild "%SOLUTION_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal
)

if %ERRORLEVEL% neq 0 (
    echo.
    echo [Error] Build failed!
    pause
    exit /b 1
)
echo.

::: Copy output files to Output folder
echo [Step 4] Copying output files to Output folder...
set "SRC_EXE=%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%"

if not exist "%SRC_EXE%" (
    echo [Error] Build output not found: %SRC_EXE%
    pause
    exit /b 1
)

copy /y "%SRC_EXE%" "%OUTPUT_DIR%\" >nul
echo   Copied %EXE_NAME%

:: Copy exe.config (required for .NET Framework runtime)
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" "%OUTPUT_DIR%\" >nul
    echo   Copied %EXE_NAME%.config
)

:: Copy rootCert.pfx (for HTTPS proxy)
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" "%OUTPUT_DIR%\" >nul
    echo   Copied rootCert.pfx
)

:: Copy logs folder (create if not exists)
if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"

:: Copy config
if exist "%PROJECT_DIR%\AppConfig.json" (
    copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%\" >nul
    echo   Copied AppConfig.json
)

:: Copy Scripts folder
if exist "%PROJECT_DIR%\Scripts\" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts\" "%OUTPUT_DIR%\Scripts\" >nul 2>&1
    echo   Copied Scripts folder
)

:: Copy Configs folder
if exist "%PROJECT_DIR%\Configs\" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs\" >nul 2>&1
)

::: Clean debug symbols (Release mode)
if "%BUILD_CONFIG%"=="Release" (
    echo.
    echo [Step 5] Cleaning debug symbols...
    if exist "%OUTPUT_DIR%\*.pdb" (
        del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1
        echo   Deleted .pdb files
    )

    if exist "%OUTPUT_DIR%\*.xml" (
        del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
        echo   Deleted .xml files
    )

)

echo.
echo ===============================================
echo   Build Complete!
echo ===============================================
echo.
echo Output: %OUTPUT_DIR%
echo Executable: %OUTPUT_DIR%\%EXE_NAME%
echo.
echo Output folder contents:
dir "%OUTPUT_DIR%" /b
echo.
echo Usage:
echo   1. Edit AppConfig.json to configure
echo   2. Run WssBarrageServer.exe
echo   3. Unity connects to ws://127.0.0.1:8888
echo.
echo ===============================================
pause
