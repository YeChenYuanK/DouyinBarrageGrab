using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Win32;
using Newtonsoft.Json;
using File = System.IO.File;

namespace BarrageGrab
{
    public static class LiveCompanHelper
    {
        /// <summary>
        /// 获取抖音直播伴侣的exe路径
        /// </summary>
        /// <returns></returns>
        public static string GetExePath()
        {
            string appName = "直播伴侣";
            appName = Path.GetFileNameWithoutExtension(appName);
            string exePath = "";

            //从卸载列表中查找
            try
            {
                // 打开注册表中的卸载信息节点
                RegistryKey uninstallNode = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

                if (uninstallNode != null)
                {
                    // 遍历所有的子键（每个子键代表一个已安装的程序的卸载信息）
                    foreach (string subKeyName in uninstallNode.GetSubKeyNames())
                    {
                        RegistryKey subKey = uninstallNode.OpenSubKey(subKeyName);
                        var displayName = subKey?.GetValue("DisplayName")?.ToString() ?? "";

                        if (!displayName.Contains(appName)) continue;
                        string uninstallString = subKey?.GetValue("UninstallString")?.ToString();
                        if (uninstallString.IsNullOrWhiteSpace()) continue;

                        //"D:\Program Files (x86)\webcast_mate\Uninstall 直播伴侣.exe" /allusers
                        //正则取出""中间的内容
                        var match = Regex.Match(uninstallString, "\"(.+?)\"");
                        if (match.Success)
                        {
                            string uninstallPath = match.Groups[1].Value;
                            string dir = Path.GetDirectoryName(uninstallPath);
                            string exe = Path.Combine(dir, $"{appName}.exe");
                            if (File.Exists(exe))
                            {
                                exePath = exe;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 发生异常时，可能是注册表访问出错，记录错误信息
                Logger.LogError(ex, $"Error checking for Live Companion installation path: {ex.Message}");
            }

            //从 C:\ProgramData\Microsoft\Windows\Start Menu\Programs 中查找
            string startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
            var findFiles = Directory.GetFiles(startMenuPath, $"{appName}.lnk", SearchOption.AllDirectories);
            if (findFiles.Length > 0)
            {
                exePath = GetShortcutTarget(findFiles[0]);
            }

            var fileName = Path.GetFileName(exePath);

            //判断是否是 版本选择器
            if (fileName.Contains("Launcher"))
            {
                var dir = Path.GetDirectoryName(exePath);
                //读取目录下 launcher_config.json
                var launcherConfigPath = Path.Combine(dir, "launcher_config.json");
                if (!File.Exists(launcherConfigPath))
                {
                    throw new Exception("未找到直播伴侣版本选择器的 launcher_config.json 文件");
                }
                var json = File.ReadAllText(launcherConfigPath, Encoding.UTF8);
                var jobj = JsonConvert.DeserializeObject<dynamic>(json);
                string curPath = jobj.cur_path;
                exePath = Path.Combine(dir, curPath, "直播伴侣.exe");
            }

            // 如果没有找到相关信息，则返回空字符串
            return exePath;
        }

        /// <summary>
        /// 获取 .lnk 文件的目标路径
        /// </summary>
        /// <param name="shortcutPath">快捷方式文件路径</param>
        /// <returns>目标路径</returns>
        private static string GetShortcutTarget(string shortcutPath)
        {
            if (string.IsNullOrEmpty(shortcutPath))
            {
                throw new ArgumentException("快捷方式路径不能为空", nameof(shortcutPath));
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-command \"$WshShell = New-Object -ComObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('{shortcutPath}'); $Shortcut.TargetPath\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(processStartInfo))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("无法启动 PowerShell 进程");
                    }

                    using (var reader = process.StandardOutput)
                    {
                        string result = reader.ReadToEnd();
                        process.WaitForExit();
                        return result.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"powershell 获取快捷方式目标路径失败，错误信息:{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 初始化直播伴侣环境
        /// </summary>
        public static void SwitchSetup()
        {
            if (!AppSetting.Current.LiveCompanHookSwitch) return;
            var exePath = GetExePath();
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.LogWarn("未找到直播伴侣.exe文件，跳过环境设置");
                return;
            }

            //设置index.js
            var indexJsPath = Path.Combine(Path.GetDirectoryName(exePath), "resources", "app", "index.js");
            Logger.LogInfo($"正在配置 " + indexJsPath);

            if (!File.Exists(indexJsPath))
            {
                throw new Exception("未找到直播伴侣的index.js文件");
            }
            var indexJs = File.ReadAllText(indexJsPath, Encoding.UTF8);

            CheckBackFile(indexJsPath);
            var newjs = SetIndexJsContent(indexJs);
            if (newjs != indexJs && !newjs.IsNullOrWhiteSpace())
            {
                File.WriteAllText(indexJsPath, newjs);
            }

            Logger.LogInfo("直播伴侣环境初始化完成");
        }

        //检测备份文件
        private static void CheckBackFile(string filePath)
        {
            if (!File.Exists(filePath)) return;

            var bakPath = filePath + ".bak";
            if (!File.Exists(bakPath))
            {
                //拷贝一份备份
                File.Copy(filePath, bakPath);
                Logger.LogInfo($"已备份 {filePath} -> {bakPath}");
            }
        }

        //设置index.js内容
        private static string SetIndexJsContent(string content)
        {
            //检测注入代理
            var mineProxyHost = $"127.0.0.1:{AppSetting.Current.ProxyPort},direct://";
            SetSwitch("proxy-server", mineProxyHost, ref content);

            //移除文件损坏检测校验
            var checkReg = new Regex(@"if\(\w{1,2}\(\w\),!\w\.ok\)");
            if (checkReg.IsMatch(content))
            {
                content = checkReg.Replace(content, "if(false)");
                Logger.LogInfo($"直播伴侣文件改动检测点1已拦截");
            }


            checkReg = new Regex(@"if\(\(0,\w.integrityCheckReport\)\(\w\),!\w\.ok\)");
            if (checkReg.IsMatch(content))
            {
                content = checkReg.Replace(content, "if(false)");
                Logger.LogInfo($"直播伴侣文件改动检测点2已拦截");
            }

            return content;
        }

        //添加 electron 启动参数
        private static void SetSwitch(string name, string value, ref string content)
        {
            if (name.IsNullOrWhiteSpace()) return;

            //检测注入
            var proxyReg = new Regex($@"(?<varname>\w+)\.commandLine\.appendSwitch\(""{name}"",""(?<value>[^""]*)""\)");
            var proxyMatch = proxyReg.Match(content);
            //检测到已经存在配置，则更新参数值
            if (proxyMatch.Success)
            {
                var matchValue = proxyMatch.Groups["value"].Value;
                if (value != matchValue)
                {
                    content = proxyReg.Replace(content, $@"${{varname}}.commandLine.appendSwitch(""{name}"",""{value}"")");
                    Logger.LogInfo($"直播伴侣成功覆盖启动参数  [{name}] = [{value}]");
                }
            }
            //否则添加新的配置
            else
            {
                var nosandboxReg = new Regex(@"(?<varname>\w+)\.commandLine\.appendSwitch\(""no-sandbox""\)");
                var match = nosandboxReg.Match(content);
                if (match.Success)
                {
                    var newvalue = $@"{match.Groups["varname"].ToString()}.commandLine.appendSwitch(""{name}"",""{value}""),";
                    content = content.Insert(match.Index, newvalue);
                    Logger.LogInfo($"直播伴侣成功添加启动参数  [{name}] = [{value}]");
                }
            }
        }

        private static void SetInnerText(HtmlNode node, string text)
        {
            node.InnerHtml = "";
            var textNode = node.OwnerDocument.CreateTextNode(text);
            node.AppendChild(textNode);
        }

        #region 快手直播伴侣支持

        /// <summary>
        /// 初始化快手直播伴侣环境（注入代理参数）
        /// </summary>
        public static void KuaishouSwitchSetup()
        {
            if (!AppSetting.Current.LiveCompanHookSwitch) return;

            var exePath = GetKuaishouExePath();
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.LogWarn("未找到快手直播伴侣(kwailive.exe)，跳过快手伴侣环境设置");
                return;
            }

            Logger.LogInfo($"找到快手直播伴侣: {exePath}");

            var resourcesDir = Path.Combine(Path.GetDirectoryName(exePath), "resources");
            var asarPath = Path.Combine(resourcesDir, "app.asar");
            var appDir = Path.Combine(resourcesDir, "app");

            // 如果已经解包过（app 目录存在），直接修改 index.js
            if (Directory.Exists(appDir))
            {
                Logger.LogInfo("检测到已解包的 app 目录，直接修改 index.js");
                PatchKuaishouIndexJs(appDir);
                return;
            }

            if (!File.Exists(asarPath))
            {
                Logger.LogWarn($"未找到 app.asar: {asarPath}，跳过快手伴侣环境设置");
                return;
            }

            // 解包 asar 到 app 目录
            Logger.LogInfo($"正在解包 {asarPath} ...");
            AsarExtract(asarPath, appDir);

            // 修改 index.js
            PatchKuaishouIndexJs(appDir);

            // 备份并禁用原 asar（Electron 优先加载同名目录）
            var asarBakPath = asarPath + ".bak";
            if (!File.Exists(asarBakPath))
            {
                File.Move(asarPath, asarBakPath);
                Logger.LogInfo($"已备份 app.asar -> app.asar.bak，Electron 将加载解包目录");
            }
            else
            {
                // bak 已存在，只需确保原 asar 不存在
                if (File.Exists(asarPath)) File.Delete(asarPath);
                Logger.LogInfo("app.asar.bak 已存在，删除 app.asar，使用解包目录");
            }

            Logger.LogInfo("快手直播伴侣环境初始化完成");
        }

        private static void PatchKuaishouIndexJs(string appDir)
        {
            // 快手 asar 结构: .vite/build/main.js 是主进程入口
            // 按优先级依次查找
            var candidates = new[]
            {
                Path.Combine(appDir, ".vite", "build", "main.js"),
                Path.Combine(appDir, "index.js"),
                Path.Combine(appDir, "main.js"),
            };

            string targetPath = null;
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { targetPath = c; break; }
            }

            // 找不到时递归搜索 main.js
            if (targetPath == null)
            {
                var found = Directory.GetFiles(appDir, "main.js", SearchOption.AllDirectories);
                if (found.Length > 0) targetPath = found[0];
            }

            if (targetPath == null)
            {
                Logger.LogWarn($"未找到快手主进程入口 js，路径: {appDir}");
                return;
            }

            Logger.LogInfo($"正在配置 {targetPath}");
            var content = File.ReadAllText(targetPath, Encoding.UTF8);
            CheckBackFile(targetPath);
            var newContent = SetIndexJsContent(content);
            if (newContent != content && !newContent.IsNullOrWhiteSpace())
            {
                File.WriteAllText(targetPath, newContent, Encoding.UTF8);
                Logger.LogInfo("快手直播伴侣主进程 js 代理参数已注入");
            }
            else
            {
                Logger.LogInfo("快手直播伴侣主进程 js 代理参数已是最新，无需修改");
            }
        }

        /// <summary>
        /// 获取快手直播伴侣的 exe 路径
        /// 查找顺序：1.配置指定路径 2.运行中的进程 3.扫描常见目录
        /// </summary>
        public static string GetKuaishouExePath()
        {
            // 1. 优先使用配置中手动指定的路径
            if (!string.IsNullOrWhiteSpace(AppSetting.Current.KuaishouLiveCompanPath))
            {
                if (File.Exists(AppSetting.Current.KuaishouLiveCompanPath))
                    return AppSetting.Current.KuaishouLiveCompanPath;
                Logger.LogWarn($"配置的快手直播伴侣路径不存在: {AppSetting.Current.KuaishouLiveCompanPath}");
            }

            // 2. 从运行中进程查找（最可靠）
            try
            {
                var procs = Process.GetProcessesByName("kwailive");
                foreach (var p in procs)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            Logger.LogInfo($"从运行进程找到快手直播伴侣: {path}");
                            return path;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn($"从进程查找快手直播伴侣失败: {ex.Message}");
            }

            // 3. 扫描所有盘符下的 KwaiLive 目录
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .Select(d => d.RootDirectory.FullName);

            foreach (var drive in drives)
            {
                var kwaiLiveDir = Path.Combine(drive, "KwaiLive", "bin");
                if (!Directory.Exists(kwaiLiveDir)) continue;

                // bin 下按版本号排序，取最新版本
                var versionDirs = Directory.GetDirectories(kwaiLiveDir)
                    .OrderByDescending(d => d)
                    .ToArray();

                foreach (var versionDir in versionDirs)
                {
                    var exePath = Path.Combine(versionDir, "kwailive.exe");
                    if (File.Exists(exePath))
                    {
                        Logger.LogInfo($"扫描盘符找到快手直播伴侣: {exePath}");
                        return exePath;
                    }
                }
            }

            // 4. 查 LocalAppData
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localKwaiDir = Path.Combine(localAppData, "KwaiLive", "bin");
            if (Directory.Exists(localKwaiDir))
            {
                var versionDirs = Directory.GetDirectories(localKwaiDir).OrderByDescending(d => d);
                foreach (var versionDir in versionDirs)
                {
                    var exePath = Path.Combine(versionDir, "kwailive.exe");
                    if (File.Exists(exePath)) return exePath;
                }
            }

            return string.Empty;
        }

        #region asar 解包实现

        /// <summary>
        /// 将 app.asar 解包到目标目录
        /// asar 格式：[4字节对齐头大小][4字节头大小][4字节对象大小][JSON头][文件内容...]
        /// </summary>
        private static void AsarExtract(string asarPath, string outputDir)
        {
            using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                // 读取头部：前16字节是 pickle 封装
                // 格式: [4: payload_size][4: header_size][4: header_obj_size][4: padding]
                var pickleSizeBytes = reader.ReadInt32(); // = 4
                var headerSize = reader.ReadInt32();
                var headerObjSize = reader.ReadInt32();
                reader.ReadInt32(); // padding（第4个Int32，字节12-15）

                // 读取 JSON 头（从字节16开始，长度 headerObjSize）
                var headerJson = Encoding.UTF8.GetString(reader.ReadBytes(headerObjSize));
                var header = Newtonsoft.Json.Linq.JObject.Parse(headerJson);

                // asar 头部实际布局：
                //   字节 0-3  : pickle outer size (=4)
                //   字节 4-7  : headerSize（pickle inner payload，含后续所有头部字段）
                //   字节 8-11 : headerObjSize（JSON 字符串长度）
                //   字节 12-15: padding（第4个 Int32）
                //   字节 16 ~ 16+headerObjSize-1 : JSON 内容
                // contentOffset = 16 + headerObjSize（按 4 字节对齐到 headerSize+8）
                long contentOffset = 16 + headerObjSize;

                // 递归提取
                if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
                AsarExtractDir(reader, header["files"] as Newtonsoft.Json.Linq.JObject, outputDir, contentOffset);
            }

            Logger.LogInfo($"asar 解包完成 -> {outputDir}");
        }

        private static void AsarExtractDir(BinaryReader reader, Newtonsoft.Json.Linq.JObject files, string outputDir, long contentOffset)
        {
            if (files == null) return;
            foreach (var prop in files.Properties())
            {
                var name = prop.Name;
                var value = prop.Value as Newtonsoft.Json.Linq.JObject;
                if (value == null) continue;

                var outPath = Path.Combine(outputDir, name);

                if (value["files"] != null)
                {
                    // 目录
                    if (!Directory.Exists(outPath)) Directory.CreateDirectory(outPath);
                    AsarExtractDir(reader, value["files"] as Newtonsoft.Json.Linq.JObject, outPath, contentOffset);
                }
                else if (value["unpacked"] != null && value["unpacked"].Value<bool>())
                {
                    // unpacked 文件已在 app.asar.unpacked 中，跳过
                }
                else if (value["offset"] != null)
                {
                    // 普通文件
                    long offset = long.Parse(value["offset"].Value<string>());
                    int size = value["size"].Value<int>();

                    reader.BaseStream.Seek(contentOffset + offset, SeekOrigin.Begin);
                    var data = reader.ReadBytes(size);

                    var dir = Path.GetDirectoryName(outPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(outPath, data);
                }
            }
        }

        #endregion

        #endregion
    }
}
