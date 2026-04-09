@echo off
chcp 65001 >nul
title DouyinBarrageGrab 构建脚本

echo ===============================================
echo   DouyinBarrageGrab - Windows 一键编译脚本
echo ===============================================
echo.

:: 检查是否在 Windows 上运行
if not "%OS%"=="Windows_NT" (
    echo [错误] 此脚本只能在 Windows 系统上运行！
    pause
    exit /b 1
)

:: 获取脚本所在目录
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%BarrageGrab"
set "SOLUTION_DIR=%SCRIPT_DIR%"
set "OUTPUT_DIR=%SCRIPT_DIR%Output"
set "EXE_NAME=WssBarrageServer.exe"

echo [信息] 项目目录: %PROJECT_DIR%
echo [信息] 输出目录: %OUTPUT_DIR%
echo.

:: 创建输出目录
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: 检查 .NET Framework
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [警告] 未检测到 .NET Framework 4.6.2+
    echo [提示] 请安装: https://dotnet.microsoft.com/download/dotnet-framework
    echo.
)

:: ========== 编译模式选择 ==========
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
echo.
echo [信息] 编译模式: %BUILD_CONFIG%
echo.

:: ========== 清理旧构建 ==========
echo [步骤1] 清理旧构建文件...
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%" (
    rd /s /q "%PROJECT_DIR%\bin\%BUILD_CONFIG%"
    echo   已清理 bin/%BUILD_CONFIG%
)
if exist "%OUTPUT_DIR%\%EXE_NAME%" (
    del /q "%OUTPUT_DIR%\%EXE_NAME%"
    echo   已清理旧输出文件
)
echo.

:: ========== 查找 MSBuild ==========
echo [步骤2] 查找 MSBuild...
set "MSBUILD_PATH="

:: 优先用 vswhere 动态查找（VS2017+/Build Tools 均支持）
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        set "MSBUILD_PATH=%%i"
    )
)

:: 备用：手动检测常见路径
if not defined MSBUILD_PATH (
    for %%p in (
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    ) do (
        if not defined MSBUILD_PATH (
            if exist %%p set "MSBUILD_PATH=%%~p"
        )
    )
)

:: 备用：Rider 内置 MSBuild
if not defined MSBUILD_PATH (
    for /d %%d in ("C:\Program Files\JetBrains\JetBrains Rider*") do (
        if exist "%%d\tools\MSBuild\Current\Bin\MSBuild.exe" (
            set "MSBUILD_PATH=%%d\tools\MSBuild\Current\Bin\MSBuild.exe"
        )
    )
)

if not defined MSBUILD_PATH (
    echo [错误] 未找到 MSBuild！
    echo [提示] 请安装以下任意一项:
    echo   - Visual Studio 2022/2019 (含"使用 C++ 的桌面开发"或".NET 桌面开发"工作负荷)
    echo   - VS Build Tools 2022: https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022
    pause
    exit /b 1
)
echo [信息] MSBuild: %MSBUILD_PATH%
echo.

:: ========== 恢复 NuGet 包 ==========
echo [步骤3] 恢复 NuGet 包...
set "NUGET_EXE=%SCRIPT_DIR%nuget.exe"

if not exist "%NUGET_EXE%" (
    echo [下载] 正在下载 nuget.exe...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing; Write-Host 'OK' } catch { Write-Host $_.Exception.Message }"
)

if not exist "%NUGET_EXE%" (
    echo [错误] nuget.exe 下载失败！
    echo [提示] 请手动下载并放到以下位置:
    echo   下载地址: https://dist.nuget.org/win-x86-commandline/latest/nuget.exe
    echo   放置位置: %NUGET_EXE%
    pause
    exit /b 1
)
echo [信息] nuget.exe: %NUGET_EXE%

"%NUGET_EXE%" restore "%PROJECT_DIR%\WssBarrageService.csproj" -PackagesDirectory "%PROJECT_DIR%\packages" -Verbosity minimal
if %ERRORLEVEL% neq 0 (
    echo [错误] NuGet 包恢复失败！
    pause
    exit /b 1
)
echo [成功] NuGet 包恢复完成。
echo.

:: ========== 编译项目 ==========
echo [步骤4] 编译项目 (Configuration=%BUILD_CONFIG%)...
echo.
"%MSBUILD_PATH%" "%SOLUTION_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal

if %ERRORLEVEL% neq 0 (
    echo.
    echo [错误] 编译失败！请检查上方错误信息。
    pause
    exit /b 1
)
echo.

:: ========== 复制输出文件 ==========
echo [步骤5] 复制输出文件...
set "SRC_EXE=%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%"

if not exist "%SRC_EXE%" (
    echo [错误] 未找到编译输出: %SRC_EXE%
    pause
    exit /b 1
)

copy /y "%SRC_EXE%" "%OUTPUT_DIR%\" >nul
echo   已复制 %EXE_NAME%

:: 复制 exe.config
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" "%OUTPUT_DIR%\" >nul
    echo   已复制 %EXE_NAME%.config
)

:: 复制 rootCert.pfx
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" "%OUTPUT_DIR%\" >nul
    echo   已复制 rootCert.pfx
)

:: 创建 logs 目录
if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"

:: 复制 AppConfig.json
if exist "%PROJECT_DIR%\AppConfig.json" (
    copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%\" >nul
    echo   已复制 AppConfig.json
)

:: 复制 Scripts 目录
if exist "%PROJECT_DIR%\Scripts\" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts\" "%OUTPUT_DIR%\Scripts\" >nul 2>&1
    echo   已复制 Scripts 目录
)

:: 复制 Configs 目录
if exist "%PROJECT_DIR%\Configs\" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs\" >nul 2>&1
)

:: 清理调试符号（Release 模式）
if "%BUILD_CONFIG%"=="Release" (
    echo.
    echo [步骤6] 清理调试文件...
    if exist "%OUTPUT_DIR%\*.pdb" del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1
    if exist "%OUTPUT_DIR%\*.xml" del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
    echo   已清理 .pdb / .xml 文件
)

echo.
echo ===============================================
echo   编译完成！
echo ===============================================
echo.
echo 输出位置: %OUTPUT_DIR%
echo 可执行文件: %OUTPUT_DIR%\%EXE_NAME%
echo.
echo 输出文件列表:
dir "%OUTPUT_DIR%" /b
echo.
echo 使用方法:
echo   1. 编辑 AppConfig.json 配置弹幕抓取参数
echo   2. 双击 WssBarrageServer.exe 运行
echo   3. Unity 项目连接 ws://127.0.0.1:8888 接收弹幕
echo.
echo ===============================================
pause
