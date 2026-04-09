@echo off
chcp 65001 >nul
pause
title DouyinBarrageGrab 编译脚本

echo ===============================================
echo   DouyinBarrageGrab - Windows 编译脚本
echo ===============================================
echo.

::: 检查操作系统
if not "%OS%"=="Windows_NT" (
    echo [错误] 此脚本只能在 Windows 上运行！
    pause
    exit /b 1
)

::: 获取目录
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "PROJECT_DIR=%SCRIPT_DIR%\BarrageGrab"
set "SOLUTION_DIR=%SCRIPT_DIR%"

::: 输出设置
set "OUTPUT_DIR=%SCRIPT_DIR%\Output"
set "EXE_NAME=WssBarrageServer.exe"

echo [信息] 项目目录: %PROJECT_DIR%
echo [信息] 输出目录: %OUTPUT_DIR%
echo.

::: 创建输出目录
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

::: 检查 .NET Framework
echo [检查] .NET Framework 4.8...
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [警告] 未检测到 .NET Framework 4.8
    echo [信息] 下载地址: https://dotnet.microsoft.com/download/dotnet-framework/net48
)
echo.

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

echo.
echo [信息] 编译模式: %BUILD_CONFIG%
echo.

::: 清理旧编译
echo [步骤 1] 清理旧编译...
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%" (
    rd /s /q "%PROJECT_DIR%\bin\%BUILD_CONFIG%"
    echo   已清理 bin/%BUILD_CONFIG%
)
if exist "%OUTPUT_DIR%\%EXE_NAME%" (
    del /q "%OUTPUT_DIR%\%EXE_NAME%"
    echo   已清理旧输出
)
echo.

::: 检查解决方案文件
if not exist "%SOLUTION_DIR%\BarrageService.sln" (
    echo [错误] 找不到 BarrageService.sln！
    pause
    exit /b 1
)
echo.

::: 查找 MSBuild
set "MSBUILD_PATH="

::: 尝试 vswhere (Visual Studio 2017+)
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
        if not defined MSBUILD_PATH set "MSBUILD_PATH=%%i"
    )
)

::: 尝试 VS 2022 MSBuild
if not defined MSBUILD_PATH (
    if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD_PATH (
    if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD_PATH (
    if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )
)

::: 尝试 VS 2019 MSBuild
if not defined MSBUILD_PATH (
    if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD_PATH (
    if exist "C:\Program Files\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD_PATH (
    if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )
)

::: 尝试 VS Build Tools 2022
if not defined MSBUILD_PATH (
    if exist "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD_PATH (
    if exist "D:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=D:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
)

::: 尝试 VS Build Tools 2019
if not defined MSBUILD_PATH (
    if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
)
if not defined MSBUILD_PATH (
    if exist "D:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=D:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
)

::: 尝试 Rider MSBuild
if not defined MSBUILD_PATH (
    for /d %%d in ("C:\Program Files\JetBrains\JetBrains Rider*") do (
        if exist "%%d\tools\MSBuild\Current\Bin\MSBuild.exe" (
            set "MSBUILD_PATH=%%d\tools\MSBuild\Current\Bin\MSBuild.exe"
        )
    )
)

if defined MSBUILD_PATH (
    echo [信息] 使用 MSBuild: %MSBUILD_PATH%
    echo.

    ::: 恢复 NuGet 包
    echo [步骤 2] 正在恢复 NuGet 包...
    set "NUGET_EXE=%SCRIPT_DIR%\nuget.exe"
    
    if not exist "%NUGET_EXE%" (
        echo [下载] 正在下载 nuget.exe...
        powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing"
    )
    
    if not exist "%NUGET_EXE%" (
        echo [错误] nuget.exe 不存在！
        echo [信息] 下载地址: https://dist.nuget.org/win-x86-commandline/latest/nuget.exe
        echo [信息] 请放到: %NUGET_EXE%
        pause
        exit /b 1
    )
    
    "%NUGET_EXE%" restore "%SOLUTION_DIR%\BarrageService.sln" -SolutionDirectory "%SOLUTION_DIR%" -NoCache
    if %ERRORLEVEL% neq 0 (
        echo [错误] NuGet 包恢复失败！
        pause
        exit /b 1
    )
    echo [成功] NuGet 包恢复完成。
    echo.

    ::: 编译项目
    echo [步骤 3] 正在编译项目 (配置=%BUILD_CONFIG%)...
    "%MSBUILD_PATH%" "%SOLUTION_DIR%\BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal
) else (
    echo [错误] 找不到 MSBuild！
    echo.
    echo [信息] 此项目需要 .NET Framework MSBuild 来编译。
    echo.
    echo [信息] 请安装以下任一工具:
    echo   1. Visual Studio 2022（社区版免费）:
    echo      https://visualstudio.microsoft.com/downloads/
    echo      - 安装时选择「.NET 桌面开发」
    echo.
    echo   2. Visual Studio Build Tools 2022:
    echo      https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022
    echo      - 选择「.NET 桌面生成工具」
    echo.
    echo   3. JetBrains Rider（自带 MSBuild）
    echo.
    pause
    exit /b 1
)

if %ERRORLEVEL% neq 0 (
    echo.
    echo [错误] 编译失败！
    pause
    exit /b 1
)
echo.

::: 复制输出文件到 Output 文件夹
echo [步骤 4] 正在复制文件到 Output 文件夹...
set "SRC_EXE=%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%"

if not exist "%SRC_EXE%" (
    echo [错误] 找不到编译输出: %SRC_EXE%
    pause
    exit /b 1
)

copy /y "%SRC_EXE%" "%OUTPUT_DIR%" >nul
echo   已复制 %EXE_NAME%

::: 复制配置文件
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" "%OUTPUT_DIR%" >nul
    echo   已复制 %EXE_NAME%.config
)

::: 复制证书文件
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" "%OUTPUT_DIR%" >nul
    echo   已复制 rootCert.pfx
)

::: 创建日志文件夹
if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"

::: 复制配置文件
if exist "%PROJECT_DIR%\AppConfig.json" (
    copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%" >nul
    echo   已复制 AppConfig.json
)

::: 复制 Scripts 文件夹
if exist "%PROJECT_DIR%\Scripts" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts" "%OUTPUT_DIR%\Scripts" >nul 2>&1
    echo   已复制 Scripts 文件夹
)

::: 复制 Configs 文件夹
if exist "%PROJECT_DIR%\Configs" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs" >nul 2>&1
)

::: 清理调试符号（Release 模式）
if "%BUILD_CONFIG%"=="Release" (
    echo.
    echo [步骤 5] 正在清理调试符号...
    if exist "%OUTPUT_DIR%\*.pdb" (
        del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1
        echo   已删除 .pdb 文件
    )
    if exist "%OUTPUT_DIR%\*.xml" (
        del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
        echo   已删除 .xml 文件
    )
)

echo.
echo ===============================================
echo   编译完成！
echo ===============================================
echo.
echo 输出目录: %OUTPUT_DIR%
echo 可执行文件: %OUTPUT_DIR%\%EXE_NAME%
echo.
echo 输出目录内容:
dir "%OUTPUT_DIR%" /b
echo.
echo 使用说明:
echo   1. 编辑 AppConfig.json 进行配置
echo   2. 运行 WssBarrageServer.exe
echo   3. Unity 连接 ws://127.0.0.1:8888
echo.
echo ===============================================
pause
