@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
title DouyinBarrageGrab 编译脚本

::: 使用 8.3 短路径格式彻底解决路径中的空格、引号和非法字符问题
for /f "tokens=*" %%a in ("%~dp0") do set "BASE_DIR=%%~fsa"
if "%BASE_DIR:~-1%"=="\" set "BASE_DIR=%BASE_DIR:~0,-1%"

::: 切换到脚本所在驱动器和目录
cd /d "%BASE_DIR%"

::: 设置变量 (全部使用相对路径或清理后的绝对路径)
set "LOG_FILE=%BASE_DIR%\build.log"
set "SOLUTION_FILE=%BASE_DIR%\BarrageService.sln"
set "PROJECT_DIR=%BASE_DIR%\BarrageGrab"
set "OUTPUT_DIR=%BASE_DIR%\Output"
set "NUGET_EXE=%BASE_DIR%\nuget.exe"
set "EXE_NAME=WssBarrageServer.exe"

echo [信息] 编译开始时间: %DATE% %TIME% > "%LOG_FILE%"

echo ===============================================
echo   DouyinBarrageGrab - Windows 编译脚本
echo ===============================================
echo [信息] 根目录: %BASE_DIR%
echo.

::: 1. 查找 MSBuild
echo [步骤 1] 正在查找编译工具...
set "MSBUILD_PATH="

::: 尝试 vswhere
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
        set "MSBUILD_PATH=%%i"
    )
)

::: 尝试常用手动路径
if not defined MSBUILD_PATH (
    for %%d in (C D E) do (
        for %%v in (2022 2019) do (
            for %%e in (Community Professional Enterprise BuildTools) do (
                if not defined MSBUILD_PATH (
                    if exist "%%d:\Program Files (x86)\Microsoft Visual Studio\%%v\%%e\MSBuild\Current\Bin\MSBuild.exe" (
                        set "MSBUILD_PATH=%%d:\Program Files (x86)\Microsoft Visual Studio\%%v\%%e\MSBuild\Current\Bin\MSBuild.exe"
                    )
                )
                if not defined MSBUILD_PATH (
                    if exist "%%d:\Program Files\Microsoft Visual Studio\%%v\%%e\MSBuild\Current\Bin\MSBuild.exe" (
                        set "MSBUILD_PATH=%%d:\Program Files\Microsoft Visual Studio\%%v\%%e\MSBuild\Current\Bin\MSBuild.exe"
                    )
                )
            )
        )
    )
)

if not defined MSBUILD_PATH (
    echo [错误] 找不到 MSBuild.exe，请确保安装了 Visual Studio 或 Build Tools。
    pause
    exit /b 1
)
echo [信息] MSBuild: "%MSBUILD_PATH%"
echo [信息] MSBuild: "%MSBUILD_PATH%" >> "%LOG_FILE%"

::: 2. 准备 NuGet
echo [步骤 2] 正在准备 NuGet...
if not exist "%NUGET_EXE%" (
    echo   [下载] 正在从 nuget.org 下载...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile 'nuget.exe' -UseBasicParsing"
)

if not exist "%NUGET_EXE%" (
    echo [错误] 下载 nuget.exe 失败，请手动下载放置在: %NUGET_EXE%
    pause
    exit /b 1
)

::: 3. 恢复包
echo [步骤 3] 正在恢复项目依赖 (NuGet Restore)...
"%NUGET_EXE%" restore "%SOLUTION_FILE%" -NoCache >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    echo [错误] NuGet 恢复失败，请查看 build.log
    pause
    exit /b 1
)

::: 4. 编译
echo [步骤 4] 正在编译项目 (Release 模式)...
"%MSBUILD_PATH%" "%SOLUTION_FILE%" /p:Configuration=Release /p:Platform="Any CPU" /t:Rebuild /v:minimal >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    echo [错误] 编译失败，请查看 build.log
    pause
    exit /b 1
)

::: 5. 整理输出
echo [步骤 5] 正在整理输出文件...
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
set "BIN_DIR=%PROJECT_DIR%\bin\Release"

if not exist "%BIN_DIR%\%EXE_NAME%" (
    echo [错误] 找不到编译生成的 EXE: %BIN_DIR%\%EXE_NAME%
    pause
    exit /b 1
)

copy /y "%BIN_DIR%\%EXE_NAME%" "%OUTPUT_DIR%\" >nul
if exist "%BIN_DIR%\%EXE_NAME%.config" copy /y "%BIN_DIR%\%EXE_NAME%.config" "%OUTPUT_DIR%\" >nul
if exist "%BIN_DIR%\rootCert.pfx" copy /y "%BIN_DIR%\rootCert.pfx" "%OUTPUT_DIR%\" >nul
if exist "%PROJECT_DIR%\AppConfig.json" copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%\" >nul
if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"

if exist "%PROJECT_DIR%\Scripts" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts" "%OUTPUT_DIR%\Scripts" >nul 2>&1
)

if exist "%PROJECT_DIR%\Configs" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs\" >nul 2>&1
)

echo.
echo ===============================================
echo   编译成功！
echo   输出目录: %OUTPUT_DIR%
echo ===============================================
echo [信息] 编译成功完成 >> "%LOG_FILE%"
pause
exit /b 0
