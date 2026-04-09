@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
title DouyinBarrageGrab 编译脚本

::: 设置日志文件
set "LOG_FILE=%~dp0build.log"
echo [信息] 编译开始时间: %DATE% %TIME% > "%LOG_FILE%"

::: 定义输出函数
set "PRINT=echo"
call :log "==============================================="
call :log "  DouyinBarrageGrab - Windows 编译脚本"
call :log "==============================================="
call :log.

::: 检查操作系统
if not "%OS%"=="Windows_NT" (
    call :log "[错误] 此脚本只能在 Windows 上运行！"
    pause
    exit /b 1
)

::: 获取目录
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "PROJECT_DIR=%SCRIPT_DIR%\BarrageGrab"
set "SOLUTION_DIR=%SCRIPT_DIR%"
set "OUTPUT_DIR=%SCRIPT_DIR%\Output"
set "EXE_NAME=WssBarrageServer.exe"

call :log "[信息] 项目目录: %PROJECT_DIR%"
call :log "[信息] 输出目录: %OUTPUT_DIR%"
call :log "[信息] 日志文件: %LOG_FILE%"
call :log.

::: 创建输出目录
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

::: 检查 .NET Framework
call :log "[检查] .NET Framework 4.8..."
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %ERRORLEVEL% neq 0 (
    call :log "[警告] 未检测到 .NET Framework 4.8"
    call :log "[信息] 下载地址: https://dotnet.microsoft.com/download/dotnet-framework/net48"
)
call :log.

::: 选择编译模式
echo 请选择编译模式:
echo   [1] Debug   - 调试版本
echo   [2] Release - 发布版本（默认）
echo.

set /p MODE_CHOICE="请输入选择 (1/2，默认 2): "
if "%MODE_CHOICE%"=="1" (
    set "BUILD_CONFIG=Debug"
) else (
    set "BUILD_CONFIG=Release"
)

call :log.
call :log "[信息] 编译模式: %BUILD_CONFIG%"
call :log.

::: 清理旧编译
call :log "[步骤 1] 清理旧编译..."
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%" (
    rd /s /q "%PROJECT_DIR%\bin\%BUILD_CONFIG%"
    call :log "  已清理 bin/%BUILD_CONFIG%"
)
if exist "%OUTPUT_DIR%\%EXE_NAME%" (
    del /q "%OUTPUT_DIR%\%EXE_NAME%"
    call :log "  已清理旧输出"
)
call :log.

::: 检查解决方案文件
if not exist "%SOLUTION_DIR%\BarrageService.sln" (
    call :log "[错误] 找不到 BarrageService.sln！"
    pause
    exit /b 1
)

::: 查找 MSBuild
set "MSBUILD_PATH="

::: 尝试 vswhere
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
        if not defined MSBUILD_PATH set "MSBUILD_PATH=%%i"
    )
)

::: 尝试常用路径 (C 盘和 D 盘)
set "SEARCH_PATHS="
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;D:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "SEARCH_PATHS=%SEARCH_PATHS%;D:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

if not defined MSBUILD_PATH (
    for %%p in (%SEARCH_PATHS%) do (
        if not defined MSBUILD_PATH (
            if exist "%%p" set "MSBUILD_PATH=%%p"
        )
    )
)

if not defined MSBUILD_PATH (
    call :log "[错误] 找不到 MSBuild！"
    call :log "[信息] 请确认是否安装了 Visual Studio 或 Build Tools (包含 .NET 桌面开发组件)。"
    pause
    exit /b 1
)

call :log "[信息] 使用 MSBuild: %MSBUILD_PATH%"
call :log.

::: 恢复 NuGet 包
call :log "[步骤 2] 正在恢复 NuGet 包..."
set "NUGET_EXE=%SCRIPT_DIR%\nuget.exe"

if not exist "%NUGET_EXE%" (
    call :log "[下载] 正在下载 nuget.exe..."
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing"
)

if not exist "%NUGET_EXE%" (
    call :log "[错误] 无法获取 nuget.exe，请手动下载并放置在 %NUGET_EXE%"
    pause
    exit /b 1
)

"%NUGET_EXE%" restore "%SOLUTION_DIR%\BarrageService.sln" -SolutionDirectory "%SOLUTION_DIR%" -NoCache >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    call :log "[错误] NuGet 包恢复失败！详情请查看 build.log"
    pause
    exit /b 1
)
call :log "[成功] NuGet 包恢复完成。"
call :log.

::: 编译项目
call :log "[步骤 3] 正在编译项目..."
"%MSBUILD_PATH%" "%SOLUTION_DIR%\BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    call :log "[错误] 编译失败！详情请查看 build.log"
    pause
    exit /b 1
)
call :log "[成功] 编译完成。"
call :log.

::: 复制输出文件
call :log "[步骤 4] 正在复制文件到 Output 文件夹..."
set "SRC_DIR=%PROJECT_DIR%\bin\%BUILD_CONFIG%"

if not exist "%SRC_DIR%\%EXE_NAME%" (
    call :log "[错误] 找不到编译输出文件: %SRC_DIR%\%EXE_NAME%"
    pause
    exit /b 1
)

copy /y "%SRC_DIR%\%EXE_NAME%" "%OUTPUT_DIR%" >nul
if exist "%SRC_DIR%\%EXE_NAME%.config" copy /y "%SRC_DIR%\%EXE_NAME%.config" "%OUTPUT_DIR%" >nul
if exist "%SRC_DIR%\rootCert.pfx" copy /y "%SRC_DIR%\rootCert.pfx" "%OUTPUT_DIR%" >nul
if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"
if exist "%PROJECT_DIR%\AppConfig.json" copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%" >nul

if exist "%PROJECT_DIR%\Scripts" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts" "%OUTPUT_DIR%\Scripts" >nul 2>&1
)

if exist "%PROJECT_DIR%\Configs" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs" >nul 2>&1
)

::: 清理调试符号
if "%BUILD_CONFIG%"=="Release" (
    call :log "[步骤 5] 正在清理调试符号..."
    if exist "%OUTPUT_DIR%\*.pdb" del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1
    if exist "%OUTPUT_DIR%\*.xml" del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
)

call :log.
call :log "==============================================="
call :log "  编译成功！"
call :log "==============================================="
call :log "[输出目录] %OUTPUT_DIR%"
call :log "[主程序] %OUTPUT_DIR%\%EXE_NAME%"
call :log.
pause
exit /b 0

::: 函数: 同时输出到屏幕和日志
:log
echo %~1
echo %~1 >> "%LOG_FILE%"
goto :eof

:log.
echo.
echo. >> "%LOG_FILE%"
goto :eof
