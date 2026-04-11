using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Globalization;

namespace BarrageGrab.Utility
{
    public class PktmonCaptureService
    {
        private readonly object syncRoot = new object();
        private string currentEtlPath;
        private bool isCapturing;
        private DateTime currentCaptureStartAt;

        public class CaptureResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string EtlPath { get; set; }
            public string PcapPath { get; set; }
            public string SniPath { get; set; }
            public List<string> TopSnis { get; set; } = new List<string>();
        }

        private class LiveControlEval
        {
            public string Level { get; set; }
            public int GifshowApiHits { get; set; }
            public int KsapisrvApiHits { get; set; }
            public int WsukwaiHits { get; set; }
            public int MateHits { get; set; }
        }

        private class DyControlEval
        {
            public string Level { get; set; }
            public int ZijieapiHits { get; set; }
            public int BytedanceHits { get; set; }
            public int AmemvHits { get; set; }
            public int DouyinCdnHits { get; set; }
        }

        private class ProxyWindowEval
        {
            public int KnownControlReqIndexCount { get; set; }
            public int HttpCandidateCount { get; set; }
        }

        private class BlindClusterStat
        {
            public string Cluster { get; set; }
            public int Hits { get; set; }
            public int UniqueHostCount { get; set; }
            public int Score { get; set; }
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
                currentCaptureStartAt = DateTime.Now;
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
            DateTime captureStartedAt;
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
                captureStartedAt = currentCaptureStartAt;
            }
            var captureStoppedAt = DateTime.Now;

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
            var snis = ExtractSnis(raw);
            var liveControl = EvaluateLiveControl(raw);
            var dyControl = EvaluateDyControl(snis);
            var proxyEval = EvaluateProxyCallbacksInWindow(captureStartedAt.AddSeconds(-2), captureStoppedAt.AddSeconds(2));
            var blindClusters = BuildBlindClusters(snis);
            var blindOk = EmitBlindArtifacts(etlPath, captureStartedAt, captureStoppedAt, snis, blindClusters, out var blindMsg);
            File.WriteAllLines(summaryPath, topSnis, Encoding.UTF8);

            lock (syncRoot)
            {
                isCapturing = false;
                currentEtlPath = null;
                currentCaptureStartAt = DateTime.MinValue;
            }

            var topText = topSnis.Any() ? string.Join("|", topSnis.Take(5)) : "EMPTY";
            Logger.LogInfo($"[KS_PKT_CAPTURE_STOP] etl={etlPath} pcap={pcapPath} sni={sniPath}");
            Logger.LogInfo($"[KS_TLS_SNI_TOP] {topText}");
            var controlLine = $"level={liveControl.Level} gifshowApi={liveControl.GifshowApiHits} ksapisrvApi={liveControl.KsapisrvApiHits} wsukwai={liveControl.WsukwaiHits} mate={liveControl.MateHits}";
            switch (liveControl.Level)
            {
                case "CONFIRMED":
                    Logger.LogInfo($"[KS_LIVE_CONTROL_CONFIRMED] {controlLine}");
                    break;
                case "WEAK":
                    Logger.LogInfo($"[KS_LIVE_CONTROL_WEAK] {controlLine}");
                    break;
                default:
                    Logger.LogInfo($"[KS_LIVE_CONTROL_NO_HIT] {controlLine}");
                    break;
            }
            var dyLine = $"level={dyControl.Level} zijieapi={dyControl.ZijieapiHits} bytedance={dyControl.BytedanceHits} amemv={dyControl.AmemvHits} douyinCdn={dyControl.DouyinCdnHits}";
            switch (dyControl.Level)
            {
                case "CONFIRMED":
                    Logger.LogInfo($"[KS_DY_CONTROL_CONFIRMED] {dyLine}");
                    break;
                case "WEAK":
                    Logger.LogInfo($"[KS_DY_CONTROL_WEAK] {dyLine}");
                    break;
                default:
                    Logger.LogInfo($"[KS_DY_CONTROL_NO_HIT] {dyLine}");
                    break;
            }

            var bypass = liveControl.Level == "CONFIRMED" && proxyEval.KnownControlReqIndexCount == 0 && proxyEval.HttpCandidateCount == 0;
            if (bypass)
            {
                Logger.LogInfo($"[KS_PREFLIGHT_HTTP_BYPASS_CONFIRMED] level=CONFIRMED knownControlReqIndex=0 httpCandidate=0 gifshowApi={liveControl.GifshowApiHits} ksapisrvApi={liveControl.KsapisrvApiHits} wsukwai={liveControl.WsukwaiHits}");
            }
            else
            {
                Logger.LogInfo($"[KS_PREFLIGHT_HTTP_BYPASS_CHECK] bypass={bypass} knownControlReqIndex={proxyEval.KnownControlReqIndexCount} httpCandidate={proxyEval.HttpCandidateCount} liveControl={liveControl.Level}");
            }
            var signalOk = EmitLiveControlSignal(
                etlPath,
                captureStartedAt,
                captureStoppedAt,
                liveControl,
                bypass,
                snis,
                blindClusters,
                out var signalMsg);
            if (signalOk)
            {
                Logger.LogInfo($"[KS_SIGNAL_EMIT_OK] {signalMsg}");
            }
            else
            {
                Logger.LogInfo($"[KS_SIGNAL_EMIT_FAIL] {signalMsg}");
            }
            var dyBypass = dyControl.Level == "CONFIRMED" && proxyEval.KnownControlReqIndexCount == 0 && proxyEval.HttpCandidateCount == 0;
            var dySignalOk = EmitDyControlSignal(etlPath, captureStartedAt, captureStoppedAt, dyControl, dyBypass, snis, blindClusters, out var dySignalMsg);
            if (dySignalOk)
            {
                Logger.LogInfo($"[KS_DY_SIGNAL_EMIT_OK] {dySignalMsg}");
            }
            else
            {
                Logger.LogInfo($"[KS_DY_SIGNAL_EMIT_FAIL] {dySignalMsg}");
            }
            if (blindClusters.Count > 0)
            {
                var topBlind = string.Join("|", blindClusters.Take(5).Select(c => $"{c.Cluster}:{c.Hits}/{c.Score}"));
                Logger.LogInfo($"[KS_BLIND_TOP] {topBlind}");
            }
            if (blindOk)
            {
                Logger.LogInfo($"[KS_BLIND_EMIT_OK] {blindMsg}");
            }
            else
            {
                Logger.LogInfo($"[KS_BLIND_EMIT_FAIL] {blindMsg}");
            }

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
            var snis = ExtractSnis(raw);
            return snis.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name)
                .Take(20)
                .Select(x => $"{x.Name} {x.Count}")
                .ToList();
        }

        private List<string> ExtractSnis(string raw)
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
            return snis;
        }

        private LiveControlEval EvaluateLiveControl(string raw)
        {
            var snis = ExtractSnis(raw);
            var eval = new LiveControlEval();
            if (snis.Count == 0)
            {
                eval.Level = "NO_HIT";
                return eval;
            }

            foreach (var s in snis)
            {
                var h = s.ToLowerInvariant();
                if ((h.Contains("apijs") && h.Contains("gifshow.com")) || h == "api3.gifshow.com")
                {
                    eval.GifshowApiHits++;
                }
                if (h.Contains("apijs") && h.Contains("ksapisrv.com")) eval.KsapisrvApiHits++;
                if (h.Contains("wsukwai.com")) eval.WsukwaiHits++;
                if (h == "mate.gifshow.com") eval.MateHits++;
            }

            if (eval.GifshowApiHits > 0 && eval.KsapisrvApiHits > 0)
            {
                eval.Level = "CONFIRMED";
                return eval;
            }
            if (eval.GifshowApiHits > 0 || eval.KsapisrvApiHits > 0 || eval.WsukwaiHits > 0)
            {
                eval.Level = "WEAK";
                return eval;
            }
            eval.Level = "NO_HIT";
            return eval;
        }

        private DyControlEval EvaluateDyControl(List<string> snis)
        {
            var eval = new DyControlEval();
            if (snis == null || snis.Count == 0)
            {
                eval.Level = "NO_HIT";
                return eval;
            }

            foreach (var s in snis)
            {
                var h = (s ?? string.Empty).ToLowerInvariant();
                if (h.Contains("zijieapi.com")) eval.ZijieapiHits++;
                if (h.Contains("bytedance.com")) eval.BytedanceHits++;
                if (h.Contains("amemv.com")) eval.AmemvHits++;
                if (h.Contains("douyincdn.com") || h.Contains("douyinpic.com")) eval.DouyinCdnHits++;
            }

            var major = 0;
            if (eval.ZijieapiHits > 0) major++;
            if (eval.BytedanceHits > 0) major++;
            if (eval.AmemvHits > 0) major++;

            if (major >= 3)
            {
                eval.Level = "CONFIRMED";
                return eval;
            }
            if (major >= 2)
            {
                eval.Level = "WEAK";
                return eval;
            }
            eval.Level = "NO_HIT";
            return eval;
        }

        private List<BlindClusterStat> BuildBlindClusters(List<string> snis)
        {
            var normalized = snis
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .ToList();
            var hostGroups = normalized
                .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var clusterHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var clusterHosts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in hostGroups)
            {
                var host = kv.Key;
                var hits = kv.Value;
                var cluster = ClusterFromHost(host);
                if (!clusterHits.ContainsKey(cluster)) clusterHits[cluster] = 0;
                clusterHits[cluster] += hits;
                if (!clusterHosts.ContainsKey(cluster))
                {
                    clusterHosts[cluster] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                clusterHosts[cluster].Add(host);
            }

            return clusterHits
                .Select(kv =>
                {
                    var unique = clusterHosts.TryGetValue(kv.Key, out var hs) ? hs.Count : 0;
                    return new BlindClusterStat
                    {
                        Cluster = kv.Key,
                        Hits = kv.Value,
                        UniqueHostCount = unique,
                        Score = kv.Value * 10 + unique * 3
                    };
                })
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Hits)
                .ThenBy(c => c.Cluster)
                .ToList();
        }

        private string ClusterFromHost(string host)
        {
            var h = (host ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(h)) return "unknown";
            if (h.Contains("ksapisrv.com")) return "ksapisrv.com";
            if (h.Contains("gifshow.com")) return "gifshow.com";
            if (h.Contains("wsukwai.com")) return "wsukwai.com";
            if (h.Contains("kuaishou.com")) return "kuaishou.com";

            var parts = h.Split('.');
            if (parts.Length >= 2)
            {
                return $"{parts[parts.Length - 2]}.{parts[parts.Length - 1]}";
            }
            return h;
        }

        private bool EmitBlindArtifacts(
            string etlPath,
            DateTime startAt,
            DateTime endAt,
            List<string> snis,
            List<BlindClusterStat> clusters,
            out string message)
        {
            message = string.Empty;
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                var traceId = Path.GetFileNameWithoutExtension(etlPath) ?? "unknown";

                var jsonlPath = Path.Combine(logsDir, "blind_control_signal.jsonl");
                var jsonlTmpPath = Path.Combine(logsDir, "blind_control_signal.fallback.jsonl");
                var topClusterJson = string.Join(",", clusters.Take(8)
                    .Select(c => $"{{\"cluster\":\"{JsonEscape(c.Cluster)}\",\"hits\":{c.Hits},\"uniqueHosts\":{c.UniqueHostCount},\"score\":{c.Score}}}"));
                var line =
                    $"{{\"schemaVersion\":\"1.0\",\"ts\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\",\"traceId\":\"{JsonEscape(traceId)}\",\"windowStart\":\"{startAt:yyyy-MM-dd HH:mm:ss.fff}\",\"windowEnd\":\"{endAt:yyyy-MM-dd HH:mm:ss.fff}\",\"totalSni\":{snis.Count},\"uniqueSni\":{snis.Distinct(StringComparer.OrdinalIgnoreCase).Count()},\"topClusters\":[{topClusterJson}]}}";

                var lineWithBreak = line + Environment.NewLine;
                Exception appendEx = null;
                var appendOk = false;
                for (var i = 0; i < 2; i++)
                {
                    try
                    {
                        File.AppendAllText(jsonlPath, lineWithBreak, Encoding.UTF8);
                        appendOk = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        appendEx = ex;
                        System.Threading.Thread.Sleep(120);
                    }
                }
                if (!appendOk)
                {
                    try
                    {
                        File.AppendAllText(jsonlTmpPath, lineWithBreak, Encoding.UTF8);
                        message = $"traceId={traceId} primary={jsonlPath} fallback={jsonlTmpPath} err={appendEx?.Message}";
                        return false;
                    }
                    catch (Exception fallbackEx)
                    {
                        message = $"traceId={traceId} primary={jsonlPath} fallback={jsonlTmpPath} err={appendEx?.Message}; fallbackErr={fallbackEx.Message}";
                        return false;
                    }
                }

                var txtPath = Path.Combine(logsDir, "blind_candidates_latest.txt");
                var sb = new StringBuilder();
                sb.AppendLine($"traceId={traceId}");
                sb.AppendLine($"window={startAt:yyyy-MM-dd HH:mm:ss.fff}~{endAt:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"totalSni={snis.Count} uniqueSni={snis.Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
                sb.AppendLine("topClusters:");
                foreach (var c in clusters.Take(12))
                {
                    sb.AppendLine($"- {c.Cluster} hits={c.Hits} uniqueHosts={c.UniqueHostCount} score={c.Score}");
                }
                File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);
                message = $"traceId={traceId} jsonl={jsonlPath} txt={txtPath}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"emit-failed: {ex.Message}";
                return false;
            }
        }

        private bool EmitLiveControlSignal(
            string etlPath,
            DateTime startAt,
            DateTime endAt,
            LiveControlEval liveControl,
            bool bypassConfirmed,
            List<string> snis,
            List<BlindClusterStat> blindClusters,
            out string message)
        {
            message = string.Empty;
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                var traceId = Path.GetFileNameWithoutExtension(etlPath) ?? "unknown";
                var jsonlPath = Path.Combine(logsDir, "live_control_signal.jsonl");
                var fallbackPath = Path.Combine(logsDir, "live_control_signal.fallback.jsonl");

                var topBlind = string.Join(",", blindClusters.Take(5)
                    .Select(c => $"{{\"cluster\":\"{JsonEscape(c.Cluster)}\",\"hits\":{c.Hits},\"score\":{c.Score}}}"));
                var uniqueSni = snis.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var version = "unknown";
                try
                {
                    version = FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).FileVersion ?? "unknown";
                }
                catch
                {
                    // best-effort only
                }

                var line =
                    $"{{\"schemaVersion\":\"1.0\",\"ts\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\",\"traceId\":\"{JsonEscape(traceId)}\",\"windowStart\":\"{startAt:yyyy-MM-dd HH:mm:ss.fff}\",\"windowEnd\":\"{endAt:yyyy-MM-dd HH:mm:ss.fff}\",\"source\":\"pktmon+tshark\",\"buildVersion\":\"{JsonEscape(version)}\",\"level\":\"{liveControl.Level}\",\"gifshowApiHits\":{liveControl.GifshowApiHits},\"ksapisrvApiHits\":{liveControl.KsapisrvApiHits},\"wsukwaiHits\":{liveControl.WsukwaiHits},\"mateHits\":{liveControl.MateHits},\"bypassConfirmed\":{bypassConfirmed.ToString().ToLowerInvariant()},\"totalSni\":{snis.Count},\"uniqueSni\":{uniqueSni},\"topBlind\":[{topBlind}]}}";
                var lineWithBreak = line + Environment.NewLine;

                Exception appendEx = null;
                var ok = false;
                for (var i = 0; i < 2; i++)
                {
                    try
                    {
                        File.AppendAllText(jsonlPath, lineWithBreak, Encoding.UTF8);
                        ok = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        appendEx = ex;
                        System.Threading.Thread.Sleep(100);
                    }
                }

                if (!ok)
                {
                    try
                    {
                        File.AppendAllText(fallbackPath, lineWithBreak, Encoding.UTF8);
                        message = $"traceId={traceId} primary={jsonlPath} fallback={fallbackPath} err={appendEx?.Message}";
                        return false;
                    }
                    catch (Exception fallbackEx)
                    {
                        message = $"traceId={traceId} primary={jsonlPath} fallback={fallbackPath} err={appendEx?.Message}; fallbackErr={fallbackEx.Message}";
                        return false;
                    }
                }

                message = $"traceId={traceId} level={liveControl.Level} bypass={bypassConfirmed} file={jsonlPath}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"emit-failed: {ex.Message}";
                return false;
            }
        }

        private bool EmitDyControlSignal(
            string etlPath,
            DateTime startAt,
            DateTime endAt,
            DyControlEval dyControl,
            bool bypassConfirmed,
            List<string> snis,
            List<BlindClusterStat> blindClusters,
            out string message)
        {
            message = string.Empty;
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                var traceId = Path.GetFileNameWithoutExtension(etlPath) ?? "unknown";
                var jsonlPath = Path.Combine(logsDir, "dy_control_signal.jsonl");
                var fallbackPath = Path.Combine(logsDir, "dy_control_signal.fallback.jsonl");

                var topBlind = string.Join(",", blindClusters.Take(5)
                    .Select(c => $"{{\"cluster\":\"{JsonEscape(c.Cluster)}\",\"hits\":{c.Hits},\"score\":{c.Score}}}"));
                var uniqueSni = snis.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var version = "unknown";
                try
                {
                    version = FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).FileVersion ?? "unknown";
                }
                catch
                {
                    // best-effort
                }

                var line =
                    $"{{\"schemaVersion\":\"1.0\",\"ts\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\",\"traceId\":\"{JsonEscape(traceId)}\",\"windowStart\":\"{startAt:yyyy-MM-dd HH:mm:ss.fff}\",\"windowEnd\":\"{endAt:yyyy-MM-dd HH:mm:ss.fff}\",\"source\":\"pktmon+tshark\",\"buildVersion\":\"{JsonEscape(version)}\",\"level\":\"{dyControl.Level}\",\"zijieapiHits\":{dyControl.ZijieapiHits},\"bytedanceHits\":{dyControl.BytedanceHits},\"amemvHits\":{dyControl.AmemvHits},\"douyinCdnHits\":{dyControl.DouyinCdnHits},\"bypassConfirmed\":{bypassConfirmed.ToString().ToLowerInvariant()},\"totalSni\":{snis.Count},\"uniqueSni\":{uniqueSni},\"topBlind\":[{topBlind}]}}";
                var lineWithBreak = line + Environment.NewLine;

                Exception appendEx = null;
                var ok = false;
                for (var i = 0; i < 2; i++)
                {
                    try
                    {
                        File.AppendAllText(jsonlPath, lineWithBreak, Encoding.UTF8);
                        ok = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        appendEx = ex;
                        System.Threading.Thread.Sleep(100);
                    }
                }
                if (!ok)
                {
                    try
                    {
                        File.AppendAllText(fallbackPath, lineWithBreak, Encoding.UTF8);
                        message = $"traceId={traceId} primary={jsonlPath} fallback={fallbackPath} err={appendEx?.Message}";
                        return false;
                    }
                    catch (Exception fallbackEx)
                    {
                        message = $"traceId={traceId} primary={jsonlPath} fallback={fallbackPath} err={appendEx?.Message}; fallbackErr={fallbackEx.Message}";
                        return false;
                    }
                }

                message = $"traceId={traceId} level={dyControl.Level} bypass={bypassConfirmed} file={jsonlPath}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"emit-failed: {ex.Message}";
                return false;
            }
        }

        private ProxyWindowEval EvaluateProxyCallbacksInWindow(DateTime startAt, DateTime endAt)
        {
            var eval = new ProxyWindowEval();
            try
            {
                var infoDir = Path.Combine(AppContext.BaseDirectory, "logs", "info");
                var logPath = Path.Combine(infoDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                if (!File.Exists(logPath)) return eval;

                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!TryParseLogTime(line, out var t)) continue;
                        if (t < startAt || t > endAt) continue;

                        if (line.Contains("[KS_HTTP_REQ_INDEX]") && line.Contains("knownControl=True"))
                        {
                            eval.KnownControlReqIndexCount++;
                        }
                        if (line.Contains("[KS_HTTP_REQ_CANDIDATE]") || line.Contains("[KS_HTTP_RESP_CANDIDATE]"))
                        {
                            if (line.IndexOf("apijs", StringComparison.OrdinalIgnoreCase) >= 0
                                || line.IndexOf("ksapisrv", StringComparison.OrdinalIgnoreCase) >= 0
                                || line.IndexOf("gifshow.com", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                eval.HttpCandidateCount++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_PREFLIGHT_HTTP_BYPASS_CHECK] evaluate-failed: {ex.Message}");
            }
            return eval;
        }

        private bool TryParseLogTime(string line, out DateTime t)
        {
            t = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(line) || line.Length < 23) return false;
            var head = line.Substring(0, 23);
            return DateTime.TryParseExact(
                head,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out t);
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
            var winDir = Environment.GetEnvironmentVariable("WINDIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                ?? @"C:\Windows";
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var candidates = new[]
            {
                Path.Combine(winDir, "Sysnative", "pktmon.exe"),
                Path.Combine(winDir, "System32", "pktmon.exe"),
                Path.Combine(winDir, "System32", "PktMon.exe"),
                string.IsNullOrWhiteSpace(systemDir) ? null : Path.Combine(systemDir, "pktmon.exe"),
                string.IsNullOrWhiteSpace(systemDir) ? null : Path.Combine(systemDir, "PktMon.exe")
            };
            foreach (var file in candidates)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                if (File.Exists(file)) return file;
            }

            var where = RunProcess("cmd.exe", "/c where pktmon.exe", 8000);
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

        private string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
