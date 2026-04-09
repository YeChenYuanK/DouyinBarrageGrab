@echo off
chcp 65001 >nul
title DouyinBarrageGrab 构建脚本

set "SCRIPT_DIR=%~dp0"
set "LOG_FILE=%SCRIPT_DIR%build.log"

:: 初始化日志
echo. > "%LOG_FILE%"

echo ===============================================
echo   DouyinBarrageGrab - Windows 一键编译脚本
echo ===============================================
echo =============================================== >> "%LOG_FILE%"
echo   DouyinBarrageGrab - Windows 一键编译脚本 >> "%LOG_FILE%"
echo =============================================== >> "%LOG_FILE%"

:: ========== 编译模式选择 ==========
echo.
echo 请选择编译模式:
echo   [1] Debug   - 调试版本
echo   [2] Release - 发布版本（默认）
echo.
set /p MODE_CHOICE="请输入选择 (1/2，默认2): "
if "%MODE_CHOICE%"=="1" (
    set "BUILD_CONFIG=Debug"
) else (
    set "BUILD_CONFIG=Release"
)
echo [信息] 编译模式: %BUILD_CONFIG%
echo [信息] 编译模式: %BUILD_CONFIG% >> "%LOG_FILE%"

:: ========== 查找 MSBuild ==========
echo.
echo [步骤1] 查找 MSBuild...
echo [步骤1] 查找 MSBuild... >> "%LOG_FILE%"
set "MSBUILD_PATH="

:: vswhere 动态查找
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
        set "MSBUILD_PATH=%%i"
    )
)

:: 备用硬编码路径
if not defined MSBUILD_PATH (
    for %%p in (
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "D:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "D:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "D:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    ) do (
        if not defined MSBUILD_PATH (
            if exist %%p set "MSBUILD_PATH=%%~p"
        )
    )
)

if not defined MSBUILD_PATH (
    echo [错误] 未找到 MSBuild！请安装 VS Build Tools 2022 并勾选 ".NET 桌面开发"
    echo [错误] 未找到 MSBuild >> "%LOG_FILE%"
    pause
    exit /b 1
)
echo [信息] MSBuild: %MSBUILD_PATH%
echo [信息] MSBuild: %MSBUILD_PATH% >> "%LOG_FILE%"

:: ========== 恢复 NuGet 包 ==========
echo.
echo [步骤2] 恢复 NuGet 包...
echo [步骤2] 恢复 NuGet 包... >> "%LOG_FILE%"

set "NUGET_EXE=%SCRIPT_DIR%nuget.exe"

if not exist "%NUGET_EXE%" (
    echo [下载] 正在下载 nuget.exe...
    echo [下载] 正在下载 nuget.exe... >> "%LOG_FILE%"
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing"
)

if not exist "%NUGET_EXE%" (
    echo [错误] nuget.exe 下载失败！请手动下载放到项目根目录
    echo [错误] nuget.exe 下载失败 >> "%LOG_FILE%"
    pause
    exit /b 1
)

"%NUGET_EXE%" restore "%SCRIPT_DIR%BarrageService.sln" -SolutionDirectory "%SCRIPT_DIR%" -NoCache
if %ERRORLEVEL% neq 0 (
    echo [错误] NuGet 包恢复失败！
    echo [错误] NuGet 包恢复失败 >> "%LOG_FILE%"
    pause
    exit /b 1
)
echo [成功] NuGet 包恢复完成
echo [成功] NuGet 包恢复完成 >> "%LOG_FILE%"

:: ========== 编译项目 ==========
echo.
echo [步骤3] 编译项目...
echo [步骤3] 编译项目... >> "%LOG_FILE%"

"%MSBUILD_PATH%" "%SCRIPT_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal
if %ERRORLEVEL% neq 0 (
    echo [错误] 编译失败！
    echo [错误] 编译失败 >> "%LOG_FILE%"
    pause
    exit /b 1
)

echo.
echo [成功] 编译完成！
echo [成功] 编译完成 >> "%LOG_FILE%"
echo 输出位置: %SCRIPT_DIR%BarrageGrab\bin\%BUILD_CONFIG%\
echo 输出位置: %SCRIPT_DIR%BarrageGrab\bin\%BUILD_CONFIG%\ >> "%LOG_FILE%"
pause
