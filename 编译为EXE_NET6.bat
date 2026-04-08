@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ========================================
echo   抖音弹幕抓取 - 一键更新编译脚本 (.NET 6)
echo ========================================
echo.

:: 检查是否为 Git 仓库
if not exist ".git" (
    echo [警告] 当前目录不是 Git 仓库，跳过 git pull
    set "SKIP_GIT=1"
) else (
    echo [1/5] 正在拉取最新代码...
    git pull
    if errorlevel 1 (
        echo [警告] Git pull 失败，继续编译本地版本
    )
)

echo.
echo [2/5] 正在清理旧构建...
cd BarrageGrab
if exist "bin" rmdir /s /q bin
if exist "obj" rmdir /s /q obj

echo.
echo [3/5] 正在恢复 NuGet 包...
dotnet restore
if errorlevel 1 (
    echo [错误] NuGet 包恢复失败！
    pause
    exit /b 1
)

echo.
echo [4/5] 正在编译并发布 (Release)...
dotnet publish -c Release -r win-x64 --self-contained
if errorlevel 1 (
    echo [错误] 编译失败！
    pause
    exit /b 1
)

echo.
echo [5/5] 正在验证输出文件...
set "OUTPUT_DIR=BarrageGrab\bin\Release\net6.0-windows\win-x64\publish"

if not exist "%OUTPUT_DIR%\WssBarrageServer.exe" (
    echo [错误] 编译产物不存在！
    pause
    exit /b 1
)

:: 复制配置文件（如果不存在）
if not exist "%OUTPUT_DIR%\AppConfig.json" (
    if exist "BarrageGrab\AppConfig.json" (
        copy "BarrageGrab\AppConfig.json" "%OUTPUT_DIR%\"
        echo [完成] 已复制 AppConfig.json
    )
)

echo.
echo ========================================
echo   编译完成！
echo ========================================
echo.
echo 输出目录: %OUTPUT_DIR%
echo.
echo 包含文件:
dir /b "%OUTPUT_DIR%" | findstr /i ".exe .dll .json .config"
echo.
echo 可直接运行 WssBarrageServer.exe
echo.
echo 按任意键打开输出目录...
pause >nul
explorer "%OUTPUT_DIR%"
