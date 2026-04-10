@echo off
chcp 65001 >nul
title DouyinBarrageGrab 编译脚本

set "SCRIPT_DIR=%~dp0"
set "LOG_FILE=%SCRIPT_DIR%build.log"
set "SOLUTION_FILE=%SCRIPT_DIR%BarrageService.sln"
set "PROJECT_DIR=%SCRIPT_DIR%BarrageGrab"
set "OUTPUT_DIR=%SCRIPT_DIR%Output"
set "NUGET_EXE=%SCRIPT_DIR%nuget.exe"
set "EXE_NAME=WssBarrageServer.exe"

echo [信息] 编译开始时间: %DATE% %TIME% > "%LOG_FILE%"
echo [信息] 根目录: %SCRIPT_DIR% >> "%LOG_FILE%"

echo ===============================================
echo   DouyinBarrageGrab - Windows 编译脚本
echo ===============================================
echo.

:: ========== 步骤0: 关闭正在运行的 exe ==========
echo [步骤0] 检查并关闭正在运行的 %EXE_NAME%...
echo [步骤0] 检查并关闭正在运行的 %EXE_NAME%... >> "%LOG_FILE%"
tasklist /FI "IMAGENAME eq %EXE_NAME%" 2>nul | findstr /i "%EXE_NAME%" >nul
if %ERRORLEVEL% equ 0 (
    taskkill /F /IM "%EXE_NAME%" >nul 2>&1
    echo [信息] 已关闭 %EXE_NAME%
    echo [信息] 已关闭 %EXE_NAME% >> "%LOG_FILE%"
    timeout /t 2 /nobreak >nul
) else (
    echo [信息] %EXE_NAME% 未在运行，跳过
    echo [信息] %EXE_NAME% 未在运行，跳过 >> "%LOG_FILE%"
)

:: ========== 步骤1: 查找 MSBuild ==========
echo [步骤1] 正在查找 MSBuild...
echo [步骤1] 正在查找 MSBuild... >> "%LOG_FILE%"
set "MSBUILD_PATH="

:: vswhere 动态查找
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
        set "MSBUILD_PATH=%%i"
    )
)

:: 备用硬编码路径（C/D 盘常见位置）
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
    echo [错误] 找不到 MSBuild.exe，请确保安装了 VS Build Tools 并勾选 .NET 桌面开发
    echo [错误] 找不到 MSBuild.exe >> "%LOG_FILE%"
    exit /b 1
)
echo [信息] MSBuild: %MSBUILD_PATH%
echo [信息] MSBuild: %MSBUILD_PATH% >> "%LOG_FILE%"

:: ========== 步骤2: 准备 NuGet ==========
echo.
echo [步骤2] 正在准备 NuGet...
echo [步骤2] 正在准备 NuGet... >> "%LOG_FILE%"

if not exist "%NUGET_EXE%" (
    echo   [下载] 正在下载 nuget.exe...
    echo   [下载] 正在下载 nuget.exe... >> "%LOG_FILE%"
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing"
)

if not exist "%NUGET_EXE%" (
    echo [错误] nuget.exe 下载失败，请手动下载放到: %NUGET_EXE%
    echo [错误] nuget.exe 下载失败 >> "%LOG_FILE%"
    exit /b 1
)
echo [信息] nuget.exe 就绪
echo [信息] nuget.exe 就绪 >> "%LOG_FILE%"

:: ========== 步骤3: 恢复 NuGet 包 ==========
echo.
echo [步骤3] 正在恢复项目依赖...
echo [步骤3] 正在恢复项目依赖... >> "%LOG_FILE%"

"%NUGET_EXE%" restore "%SOLUTION_FILE%" -NoCache >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    echo [错误] NuGet 恢复失败，请查看 build.log
    echo [错误] NuGet 恢复失败 >> "%LOG_FILE%"
    exit /b 1
)
echo [成功] NuGet 恢复完成
echo [成功] NuGet 恢复完成 >> "%LOG_FILE%"

:: ========== 步骤4: 编译 ==========
echo.
echo [步骤4] 正在编译项目 (Release)...
echo [步骤4] 正在编译项目 (Release)... >> "%LOG_FILE%"

"%MSBUILD_PATH%" "%SOLUTION_FILE%" /p:Configuration=Release /p:Platform="Any CPU" /t:Rebuild /v:minimal >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    echo [错误] 编译失败，请查看 build.log
    echo [错误] 编译失败 >> "%LOG_FILE%"
    exit /b 1
)

:: ========== 步骤5: 整理输出 ==========
echo.
echo [步骤5] 正在整理输出文件...
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: 保存 logs 子目录
if exist "%OUTPUT_DIR%\logs" (
    move "%OUTPUT_DIR%\logs" "%OUTPUT_DIR%\logs_bak" >nul 2>&1
)

:: 清空 Output 目录（删除所有文件和子目录）
echo [信息] 清空 Output 目录...
for %%f in ("%OUTPUT_DIR%\*") do del /f /q "%%f" >nul 2>&1
for /d %%d in ("%OUTPUT_DIR%\*") do rmdir /s /q "%%d" >nul 2>&1

:: 恢复 logs 子目录
if exist "%OUTPUT_DIR%\logs_bak" (
    move "%OUTPUT_DIR%\logs_bak" "%OUTPUT_DIR%\logs" >nul 2>&1
)
if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"

set "BIN_DIR=%PROJECT_DIR%\bin\Release"

if not exist "%BIN_DIR%\%EXE_NAME%" (
    echo [错误] 找不到编译输出: %BIN_DIR%\%EXE_NAME%
    echo [错误] 找不到编译输出: %BIN_DIR%\%EXE_NAME% >> "%LOG_FILE%"
    exit /b 1
)

:: 复制编译产物
copy /y "%BIN_DIR%\%EXE_NAME%" "%OUTPUT_DIR%\" >nul
if exist "%BIN_DIR%\%EXE_NAME%.config" copy /y "%BIN_DIR%\%EXE_NAME%.config" "%OUTPUT_DIR%\" >nul
if exist "%BIN_DIR%\rootCert.pfx" copy /y "%BIN_DIR%\rootCert.pfx" "%OUTPUT_DIR%\" >nul

echo.
echo ===============================================
echo   编译成功！
echo   输出目录: %OUTPUT_DIR%
echo ===============================================
echo [信息] 编译成功完成 >> "%LOG_FILE%"

exit /b 0
