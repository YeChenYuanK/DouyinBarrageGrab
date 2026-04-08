@echo off
chcp 65001 >nul
title DouyinBarrageGrab 构建脚本

echo ===============================================
echo   DouyinBarrageGrab - Windows 一键编译脚本
echo ===============================================
echo.

:: 检查是否在 Windows 上运行
echo [检查] 操作系统...
if not "%OS%"=="Windows_NT" (
    echo [错误] 此脚本只能在 Windows 系统上运行！
    pause
    exit /b 1
)

:: 获取脚本所在目录
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%BarrageGrab"
set "SOLUTION_DIR=%SCRIPT_DIR%"

:: 设置输出目录
set "OUTPUT_DIR=%SCRIPT_DIR%Output"
set "EXE_NAME=WssBarrageServer.exe"

echo [信息] 项目目录: %PROJECT_DIR%
echo [信息] 输出目录: %OUTPUT_DIR%
echo.

:: 创建输出目录
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: 检查 .NET Framework
echo [检查] .NET Framework 4.6.2...
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [警告] 未检测到 .NET Framework 4.6.2+
    echo [提示] 请安装 .NET Framework 4.6.2 或更高版本
    echo [下载] https://dotnet.microsoft.com/download/dotnet-framework/net462
)
echo.

:: ========== 编译模式选择 ==========
echo 请选择编译模式:
echo   [1] Debug   - 调试版本（含调试信息）
echo   [2] Release - 发布版本（优化，删除调试文件）
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
    echo   已清理输出目录中的旧文件
)
echo.

:: ========== 恢复 NuGet 包 ==========
echo [步骤2] 恢复 NuGet 包...
if exist "%SOLUTION_DIR%BarrageService.sln" (
    nuget restore "%SOLUTION_DIR%BarrageService.sln"
    if %ERRORLEVEL% neq 0 (
        echo [错误] NuGet 包恢复失败！
        echo [提示] 尝试手动运行: nuget restore BarrageService.sln
        pause
        exit /b 1
    )
    echo   NuGet 包恢复成功
) else (
    echo [错误] 未找到 BarrageService.sln 解决方案文件！
    pause
    exit /b 1
)
echo.

:: ========== 编译项目 ==========
echo [步骤3] 编译项目 (Configuration=%BUILD_CONFIG%)...
echo.

:: 尝试使用 MSBuild（Visual Studio 自带）
where msbuild >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo [信息] 使用 MSBuild 编译...
    msbuild "%SOLUTION_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="AnyCPU" /t:Rebuild /v:minimal
) else (
    :: 尝试使用 dotnet MSBuild
    where dotnet >nul 2>&1
    if %ERRORLEVEL% equ 0 (
        echo [信息] 使用 dotnet MSBuild 编译...
        dotnet msbuild "%SOLUTION_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="AnyCPU" /t:Rebuild /v:minimal
    ) else (
        echo [错误] 未找到 MSBuild 或 dotnet 命令！
        echo [提示] 请安装 Visual Studio (含 MSBuild) 或 .NET SDK
        echo [下载] https://visualstudio.microsoft.com/downloads/
        pause
        exit /b 1
    )
)

if %ERRORLEVEL% neq 0 (
    echo.
    echo [错误] 编译失败！请检查上方错误信息。
    pause
    exit /b 1
)
echo.

:: ========== 复制输出文件 ==========
echo [步骤4] 复制输出文件...
set "SRC_EXE=%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%"

if not exist "%SRC_EXE%" (
    echo [错误] 未找到编译输出: %SRC_EXE%
    echo [提示] 检查编译日志，确认是否成功
    pause
    exit /b 1
)

copy /y "%SRC_EXE%" "%OUTPUT_DIR%\" >nul
echo   已复制 %EXE_NAME%

:: 复制配置文件
if exist "%PROJECT_DIR%\AppConfig.json" (
    copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%\" >nul
    echo   已复制 AppConfig.json
)

:: 复制脚本文件（如果存在）
if exist "%PROJECT_DIR%\Scripts\" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts\" "%OUTPUT_DIR%\Scripts\" >nul 2>&1
    echo   已复制 Scripts 目录
)

:: 复制依赖文件
if exist "%PROJECT_DIR%\Configs\" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y /e "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs\" >nul 2>&1
)

:: 删除调试符号（Release 模式）
if "%BUILD_CONFIG%"=="Release" (
    echo.
    echo [步骤5] 清理调试文件...
    if exist "%OUTPUT_DIR%\*.pdb" (
        del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1
        echo   已删除 .pdb 符号文件
    )
    if exist "%OUTPUT_DIR%\*.xml" (
        del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
        echo   已删除 .xml 文档文件
    )
)

echo.
echo ===============================================
echo   ✅ 编译完成！
echo ===============================================
echo.
echo 输出位置: %OUTPUT_DIR%
echo 可执行文件: %OUTPUT_DIR%\%EXE_NAME%
echo.
echo 使用方法:
echo   1. 编辑 AppConfig.json 配置弹幕抓取参数
echo   2. 双击 WssBarrageServer.exe 运行
echo   3. Unity 项目连接 ws://127.0.0.1:8888 接收弹幕
echo.
echo 快手配置示例 (AppConfig.json):
echo   "kuaishou": {
echo     "enabled": true,
echo     "roomId": "你的快手号",
echo     "cookie": ""
echo   }
echo.
echo ===============================================
pause
