using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;

namespace BarrageGrab.Utility
{
    public class PktmonCaptureService
    {
        private readonly object syncRoot = new object();
        private string currentEtlPath;
        private bool isCapturing;

        public class CaptureResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string EtlPath { get; set; }
            public string PcapPath { get; set; }
            public string SniPath { get; set; }
            public List<string> TopSnis { get; set; } = new List<string>();
        }

        private class ProcessResult
        {
            public int ExitCode { get; set; }
            public string Stdout { get; set; }
            public string Stderr { get; set; }
        }

        public bool IsCapturing
        {
            get
            {
                lock (syncRoot)
                {
                    return isCapturing;
                }
            }
        }

        public CaptureResult StartCapture()
        {
            lock (syncRoot)
            {
                if (isCapturing)
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Message = "已在抓包中，请先停止后再开始。"
                    };
                }
            }

            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var etlPath = Path.Combine(logsDir, $"pktmon_live_{ts}.etl");
            var pktmonPath = ResolvePktmonPath();
            if (string.IsNullOrWhiteSpace(pktmonPath))
            {
                return new CaptureResult
                {
                    Success = false,
                    Message = "未找到 pktmon.exe，请确认系统为 Windows 10/11 且具备该组件。"
                };
            }

            RunProcess(pktmonPath, "stop", 5000);
            RunProcess(pktmonPath, "filter remove", 5000);

            var add443 = RunProcess(pktmonPath, "filter add -p 443", 10000);
            if (add443.ExitCode != 0)
            {
                return new CaptureResult
                {
                    Success = false,
                    Message = "设置 pktmon 443 过滤器失败，请确认以管理员权限运行。"
                };
            }

            var add1935 = RunProcess(pktmonPath, "filter add -p 1935", 10000);
            if (add1935.ExitCode != 0)
            {
                return new CaptureResult
                {
                    Success = false,
                    Message = "设置 pktmon 1935 过滤器失败，请确认以管理员权限运行。"
                };
            }

            var start = RunProcess(pktmonPath, $"start --etw --pkt-size 0 --file-name \"{etlPath}\"", 15000);
            if (start.ExitCode != 0)
            {
                return new CaptureResult
                {
                    Success = false,
                    Message = $"启动 pktmon 失败：{FirstNonEmpty(start.Stderr, start.Stdout)}"
                };
            }

            lock (syncRoot)
            {
                currentEtlPath = etlPath;
                isCapturing = true;
            }

            Logger.LogInfo($"[KS_PKT_CAPTURE_START] etl={etlPath}");
            return new CaptureResult
            {
                Success = true,
                Message = "抓包已开始。",
                EtlPath = etlPath
            };
        }

        public CaptureResult StopAndAnalyze()
        {
            string etlPath;
            lock (syncRoot)
            {
                if (!isCapturing || string.IsNullOrWhiteSpace(currentEtlPath))
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Message = "当前没有正在进行的抓包会话。"
                    };
                }
                etlPath = currentEtlPath;
            }

            var pktmonPath = ResolvePktmonPath();
            if (string.IsNullOrWhiteSpace(pktmonPath))
            {
                return new CaptureResult
                {
                    Success = false,
                    Message = "未找到 pktmon.exe，无法停止并分析。"
                };
            }

            var stop = RunProcess(pktmonPath, "stop", 10000);
            if (stop.ExitCode != 0)
            {
                return new CaptureResult
                {
                    Success = false,
                    Message = $"停止 pktmon 失败：{FirstNonEmpty(stop.Stderr, stop.Stdout)}"
                };
            }

            var pcapPath = Path.ChangeExtension(etlPath, ".pcapng");
            var sniPath = Path.ChangeExtension(etlPath, ".sni.txt");
            var summaryPath = Path.ChangeExtension(etlPath, ".sni.summary.txt");

            var etl2pcap = RunProcess(pktmonPath, $"etl2pcap \"{etlPath}\" -o \"{pcapPath}\"", 30000);
            if (etl2pcap.ExitCode != 0 || !File.Exists(pcapPath))
            {
                lock (syncRoot)
                {
                    isCapturing = false;
                    currentEtlPath = null;
                }
                return new CaptureResult
                {
                    Success = false,
                    EtlPath = etlPath,
                    Message = $"etl2pcap 失败：{FirstNonEmpty(etl2pcap.Stderr, etl2pcap.Stdout)}"
                };
            }

            var tsharkPath = ResolveTsharkPath();
            if (string.IsNullOrWhiteSpace(tsharkPath))
            {
                lock (syncRoot)
                {
                    isCapturing = false;
                    currentEtlPath = null;
                }
                return new CaptureResult
                {
                    Success = false,
                    EtlPath = etlPath,
                    PcapPath = pcapPath,
                    Message = "未找到 tshark.exe，请先安装 Wireshark。"
                };
            }

            var tsharkArgs = $"-r \"{pcapPath}\" -Y \"tls.handshake.type==1 && tls.handshake.extensions_server_name\" -T fields -E separator=| -e frame.time -e ip.dst -e tcp.dstport -e tls.handshake.extensions_server_name -e tls.handshake.extensions_alpn_str";
            var tshark = RunProcess(tsharkPath, tsharkArgs, 45000);
            var raw = tshark.Stdout ?? string.Empty;
            File.WriteAllText(sniPath, raw, Encoding.UTF8);

            var topSnis = BuildTopSnis(raw);
            File.WriteAllLines(summaryPath, topSnis, Encoding.UTF8);

            lock (syncRoot)
            {
                isCapturing = false;
                currentEtlPath = null;
            }

            var topText = topSnis.Any() ? string.Join("|", topSnis.Take(5)) : "EMPTY";
            Logger.LogInfo($"[KS_PKT_CAPTURE_STOP] etl={etlPath} pcap={pcapPath} sni={sniPath}");
            Logger.LogInfo($"[KS_TLS_SNI_TOP] {topText}");

            return new CaptureResult
            {
                Success = true,
                Message = "抓包与 SNI 分析完成。",
                EtlPath = etlPath,
                PcapPath = pcapPath,
                SniPath = sniPath,
                TopSnis = topSnis
            };
        }

        private List<string> BuildTopSnis(string raw)
        {
            var lines = (raw ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var snis = new List<string>();
            foreach (var line in lines)
            {
                var cols = line.Split('|');
                if (cols.Length < 4) continue;
                var sni = cols[3].Trim();
                if (string.IsNullOrWhiteSpace(sni)) continue;
                snis.Add(sni);
            }

            return snis.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name)
                .Take(20)
                .Select(x => $"{x.Name} {x.Count}")
                .ToList();
        }

        private string ResolveTsharkPath()
        {
            var candidates = new[]
            {
                @"C:\Program Files\Wireshark\tshark.exe",
                @"C:\Program Files (x86)\Wireshark\tshark.exe"
            };
            foreach (var file in candidates)
            {
                if (File.Exists(file)) return file;
            }

            var where = RunProcess("cmd.exe", "/c where tshark", 8000);
            if (where.ExitCode != 0 || string.IsNullOrWhiteSpace(where.Stdout)) return null;
            var first = where.Stdout
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return File.Exists(first) ? first : null;
        }

        private string ResolvePktmonPath()
        {
            var system32 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "pktmon.exe");
            if (File.Exists(system32)) return system32;

            var where = RunProcess("cmd.exe", "/c where pktmon", 8000);
            if (where.ExitCode != 0 || string.IsNullOrWhiteSpace(where.Stdout)) return null;
            var first = where.Stdout
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return File.Exists(first) ? first : null;
        }

        private ProcessResult RunProcess(string fileName, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = psi;
                try
                {
                    process.Start();
                }
                catch (Win32Exception ex)
                {
                    return new ProcessResult
                    {
                        ExitCode = -1,
                        Stdout = string.Empty,
                        Stderr = $"启动失败: {fileName} {arguments}; {ex.Message}"
                    };
                }
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return new ProcessResult
                    {
                        ExitCode = -1,
                        Stdout = stdout,
                        Stderr = $"命令超时：{fileName} {arguments}"
                    };
                }
                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Stdout = stdout,
                    Stderr = stderr
                };
            }
        }

        private string FirstNonEmpty(string first, string second)
        {
            if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
            if (!string.IsNullOrWhiteSpace(second)) return second.Trim();
            return "未知错误";
        }
    }
}
