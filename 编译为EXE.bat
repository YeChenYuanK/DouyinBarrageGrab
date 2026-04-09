@echo off
chcp 65001 >nul
title DouyinBarrageGrab 构建脚本

:: 获取脚本所在目录
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%BarrageGrab"
set "SOLUTION_DIR=%SCRIPT_DIR%"
set "OUTPUT_DIR=%SCRIPT_DIR%Output"
set "EXE_NAME=WssBarrageServer.exe"
set "LOG_FILE=%SCRIPT_DIR%build.log"

:: 初始化日志文件
echo. > "%LOG_FILE%"

:: 用 call :log 同时输出到屏幕和日志的宏
:: 注意：所有 echo 改为调用 :log

call :log "==============================================="
call :log "  DouyinBarrageGrab - Windows 一键编译脚本"
call :log "==============================================="
call :log ""
call :log "[信息] 项目目录: %PROJECT_DIR%"
call :log "[信息] 输出目录: %OUTPUT_DIR%"
call :log "[信息] 日志文件: %LOG_FILE%"
call :log ""

:: 检查是否在 Windows 上运行
if not "%OS%"=="Windows_NT" (
    call :log "[错误] 此脚本只能在 Windows 系统上运行！"
    pause
    exit /b 1
)

:: 创建输出目录
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: 检查 .NET Framework
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if %ERRORLEVEL% neq 0 (
    call :log "[警告] 未检测到 .NET Framework 4.6.2+"
    call :log "[提示] 请安装: https://dotnet.microsoft.com/download/dotnet-framework"
    call :log ""
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
call :log ""
call :log "[信息] 编译模式: %BUILD_CONFIG%"
call :log ""

:: ========== 清理旧构建 ==========
call :log "[步骤1] 清理旧构建文件..."
if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%" (
    rd /s /q "%PROJECT_DIR%\bin\%BUILD_CONFIG%"
    call :log "  已清理 bin/%BUILD_CONFIG%"
)
if exist "%OUTPUT_DIR%\%EXE_NAME%" (
    del /q "%OUTPUT_DIR%\%EXE_NAME%"
    call :log "  已清理旧输出文件"
)
call :log ""

:: ========== 查找 MSBuild ==========
call :log "[步骤2] 查找 MSBuild..."
set "MSBUILD_PATH="

:: 优先用 vswhere 动态查找
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
        set "MSBUILD_PATH=%%i"
    )
)

:: 备用：手动检测常见路径（含 BuildTools，C/D 盘）
if not defined MSBUILD_PATH (
    for %%p in (
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
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

:: 备用：Rider 内置 MSBuild
if not defined MSBUILD_PATH (
    for /d %%d in ("C:\Program Files\JetBrains\JetBrains Rider*") do (
        if exist "%%d\tools\MSBuild\Current\Bin\MSBuild.exe" (
            set "MSBUILD_PATH=%%d\tools\MSBuild\Current\Bin\MSBuild.exe"
        )
    )
)

if not defined MSBUILD_PATH (
    call :log "[错误] 未找到 MSBuild！"
    call :log ""
    call :log "[诊断] 检查以下路径是否存在 MSBuild.exe:"
    call :log "  C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\"
    call :log "  C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\"
    call :log ""
    call :log "[提示] 请安装以下任意一项（勾选'.NET 桌面开发'工作负荷）:"
    call :log "  VS Build Tools 2022: https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022"
    pause
    exit /b 1
)
call :log "[信息] MSBuild: %MSBUILD_PATH%"
call :log ""

:: ========== 恢复 NuGet 包 ==========
call :log "[步骤3] 恢复 NuGet 包..."
set "NUGET_EXE=%SCRIPT_DIR%nuget.exe"

if not exist "%NUGET_EXE%" (
    call :log "[下载] 正在下载 nuget.exe..."
    powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing; Write-Host 'OK' } catch { Write-Host $_.Exception.Message }" >> "%LOG_FILE%" 2>&1
    powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET_EXE%' -UseBasicParsing } catch { exit 1 }"
)

if not exist "%NUGET_EXE%" (
    call :log "[错误] nuget.exe 下载失败！"
    call :log "[提示] 请手动下载: https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"
    call :log "        放置位置: %NUGET_EXE%"
    pause
    exit /b 1
)
call :log "[信息] nuget.exe: %NUGET_EXE%"

"%NUGET_EXE%" restore "%PROJECT_DIR%\WssBarrageService.csproj" -PackagesDirectory "%PROJECT_DIR%\packages" -NoCache >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    call :log "[错误] NuGet 包恢复失败！详情见 build.log"
    pause
    exit /b 1
)
call :log "[成功] NuGet 包恢复完成。"
call :log ""

:: ========== 编译项目 ==========
call :log "[步骤4] 编译项目 (Configuration=%BUILD_CONFIG%)..."
call :log ""

"%MSBUILD_PATH%" "%SOLUTION_DIR%BarrageService.sln" /p:Configuration=%BUILD_CONFIG% /p:Platform="Any CPU" /t:Rebuild /v:minimal >> "%LOG_FILE%" 2>&1
if %ERRORLEVEL% neq 0 (
    call :log ""
    call :log "[错误] 编译失败！详情见 build.log"
    pause
    exit /b 1
)
call :log ""

:: ========== 复制输出文件 ==========
call :log "[步骤5] 复制输出文件..."
set "SRC_EXE=%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%"

if not exist "%SRC_EXE%" (
    call :log "[错误] 未找到编译输出: %SRC_EXE%"
    pause
    exit /b 1
)

copy /y "%SRC_EXE%" "%OUTPUT_DIR%\" >nul
call :log "  已复制 %EXE_NAME%"

if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\%EXE_NAME%.config" "%OUTPUT_DIR%\" >nul
    call :log "  已复制 %EXE_NAME%.config"
)

if exist "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" (
    copy /y "%PROJECT_DIR%\bin\%BUILD_CONFIG%\rootCert.pfx" "%OUTPUT_DIR%\" >nul
    call :log "  已复制 rootCert.pfx"
)

if not exist "%OUTPUT_DIR%\logs" mkdir "%OUTPUT_DIR%\logs"

if exist "%PROJECT_DIR%\AppConfig.json" (
    copy /y "%PROJECT_DIR%\AppConfig.json" "%OUTPUT_DIR%\" >nul
    call :log "  已复制 AppConfig.json"
)

if exist "%PROJECT_DIR%\Scripts\" (
    if not exist "%OUTPUT_DIR%\Scripts" mkdir "%OUTPUT_DIR%\Scripts"
    xcopy /y /e /q "%PROJECT_DIR%\Scripts\" "%OUTPUT_DIR%\Scripts\" >nul 2>&1
    call :log "  已复制 Scripts 目录"
)

if exist "%PROJECT_DIR%\Configs\" (
    if not exist "%OUTPUT_DIR%\Configs" mkdir "%OUTPUT_DIR%\Configs"
    copy /y "%PROJECT_DIR%\Configs\*" "%OUTPUT_DIR%\Configs\" >nul 2>&1
)

:: 清理调试符号（Release 模式）
if "%BUILD_CONFIG%"=="Release" (
    call :log ""
    call :log "[步骤6] 清理调试文件..."
    if exist "%OUTPUT_DIR%\*.pdb" del /q "%OUTPUT_DIR%\*.pdb" >nul 2>&1
    if exist "%OUTPUT_DIR%\*.xml" del /q "%OUTPUT_DIR%\*.xml" >nul 2>&1
    call :log "  已清理 .pdb / .xml 文件"
)

call :log ""
call :log "==============================================="
call :log "  编译完成！"
call :log "==============================================="
call :log ""
call :log "输出位置: %OUTPUT_DIR%"
call :log "可执行文件: %OUTPUT_DIR%\%EXE_NAME%"
call :log "日志文件: %LOG_FILE%"
call :log ""
pause
goto :eof

:: ========== 日志函数：同时输出到屏幕和日志文件 ==========
:log
echo %~1
echo %~1 >> "%LOG_FILE%"
goto :eof
