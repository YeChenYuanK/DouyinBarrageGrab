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
    echo [1/6] 正在拉取最新代码...
    git pull
    if errorlevel 1 (
        echo [警告] Git pull 失败，继续编译本地版本
    )
)

echo.
echo [2/6] 正在清理旧构建...
cd BarrageGrab
if exist "bin" rmdir /s /q bin
if exist "obj" rmdir /s /q obj
cd ..

:: 清理旧的 Output 目录
if exist "Output" rmdir /s /q Output

echo.
echo [3/6] 正在恢复 NuGet 包...
dotnet restore BarrageGrab/WssBarrageService.csproj
if errorlevel 1 (
    echo [错误] NuGet 包恢复失败！
    pause
    exit /b 1
)

echo.
echo [4/6] 正在编译并发布 (Release)...
dotnet publish BarrageGrab/WssBarrageService.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
if errorlevel 1 (
    echo [错误] 编译失败！
    pause
    exit /b 1
)

echo.
echo [5/6] 正在创建精简发布包...
set "SOURCE_DIR=BarrageGrab\bin\Release\net6.0-windows\win-x64\publish"
set "OUTPUT_DIR=Output"

:: 创建输出目录
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: 复制 EXE（单文件模式只有一个 EXE）
if exist "%SOURCE_DIR%\WssBarrageServer.exe" (
    copy "%SOURCE_DIR%\WssBarrageServer.exe" "%OUTPUT_DIR%\" >nul
    echo [完成] 复制 WssBarrageServer.exe
) else (
    echo [错误] 编译产物不存在！
    pause
    exit /b 1
)

:: 复制配置文件
if exist "BarrageGrab\AppConfig.json" (
    copy "BarrageGrab\AppConfig.json" "%OUTPUT_DIR%\" >nul
    echo [完成] 复制 AppConfig.json
)

:: 复制根证书（如果存在）
if exist "BarrageGrab\rootCert.pfx" (
    copy "BarrageGrab\rootCert.pfx" "%OUTPUT_DIR%\" >nul
    echo [完成] 复制 rootCert.pfx
)

:: 复制证书（如果存在）
if exist "BarrageGrab\*.pfx" (
    for %%f in (BarrageGrab\*.pfx) do (
        copy "%%f" "%OUTPUT_DIR%\" >nul
        echo [完成] 复制 %%~nxf
    )
)

echo.
echo [6/6] 验证输出文件...
echo.
echo ========================================
echo   编译完成！
echo ========================================
echo.
echo 输出目录: %cd%\%OUTPUT_DIR%
echo.
echo 包含文件:
dir /b "%OUTPUT_DIR%"
echo.
echo 使用方法:
echo   1. 双击 WssBarrageServer.exe 运行
echo   2. 编辑 AppConfig.json 自定义配置
echo   3. Unity 项目连接: ws://127.0.0.1:8888
echo.
echo 按任意键打开 Output 目录...
pause >nul
explorer "%OUTPUT_DIR%"
