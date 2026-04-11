using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BarrageGrab.Modles;
using BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Proxy.ProxyEventArgs;
using BrotliSharpLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog.LayoutRenderers;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace BarrageGrab.Proxy
{
    internal class TitaniumProxy : SystemProxy
    {
        ProxyServer proxyServer = null;
        ExplicitProxyEndPoint explicitEndPoint = null;
        ExternalProxy upStreamProxy = null;
        List<string> rinfoRequestings = new List<string>();

        const string SCRIPT_HOST = "lf-cdn-tos.bytescm.com";
        const string LIVE_HOST = "live.douyin.com";
        const string DOUYIN_HOST = "www.douyin.com";
        const string USER_INFO_PATH = "/webcast/user/me/";
        const string BARRAGE_POOL_PATH = "/webcast/im/fetch";
        const string LIVE_SCRIPT_PATH = "/obj/static/webcast/douyin_live";
        const string WEBCAST_AMEMV_HOST = "webcast.amemv.com";

        // ---- 快手直播域名 ----
        const string KS_LIVE_HOST = "live.kuaishou.com";         // 快手网页直播
        const string KS_MOBILE_HOST = "m.gifshow.com";            // 快手移动端直播
        const string KS_WS_HOST = "live-ws-group.kuaishou.com";  // 快手弹幕WS服务器
        const string KS_API_HOST = "live.kuaishou.com";           // 快手API

        private readonly Regex webcastBarrageReg = new Regex(@"webcast\d+-ws-web-\w+\.(douyin|amemv)\.com");

        // 快手弹幕 WebSocket 地址正则（用于识别和拦截快手弹幕流）
        private readonly Regex ksBarrageReg = new Regex(@"(live-ws.*\.kuaishou\.com|livejs-ws\.kuaishou\.cn|.*ws.*kuaishou.*|.*kuaishou.*ws.*|.*wsukwai\.com.*|/websocket|/group\d+)");
        private readonly object ksHttpReqIndexLock = new object();
        private readonly Dictionary<string, DateTime> ksHttpReqIndexLastAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // 快手直播弹幕域名列表（用于识别快手弹幕请求）
        private readonly string[] ksBarrageHosts = new[] {
            // WebSocket 弹幕服务器（kuaishou.com）
            "live-ws-group.kuaishou.com",
            "live-ws.kuaishou.com",
            "live-ws-pg-group1.kuaishou.com",
            "live-ws-pg-group2.kuaishou.com",
            "live-ws-pg-group3.kuaishou.com",
            "live-ws-pg-group4.kuaishou.com",
            "live-ws-pg-group5.kuaishou.com",
            "livejs-ws.kuaishou.cn",
            // WebSocket 弹幕服务器（wsukwai.com，快手直播伴侣实际使用）
            "wsukwai.com",
            "p3-live.wsukwai.com",
            "live.wsukwai.com",
            // API 和页面
            "live.kuaishou.com",
            "m.gifshow.com",
            "www.kuaishou.com",
            // CDN 和资源
            "cdn.gifshow.com",
            "tx2.a.kwimgs.com",
            "ali.a.kwimgs.com",
            "static.yximgs.com",
        };

        public override string HttpUpstreamProxy { get { return proxyServer?.UpStreamHttpProxy?.ToString() ?? ""; } }

        public override string HttpsUpstreamProxy { get { return proxyServer?.UpStreamHttpsProxy?.ToString() ?? ""; } }

        static TitaniumProxy()
        {
            // 设置代理过滤规则
            string[] bypassList = { "localhost", "127.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                                "172.20.*", "172.21.*", "172.22.*", "172.23.*", "172.24.*", "172.25.*",
                                "172.26.*", "172.27.*", "172.28.*", "172.29.*", "172.30.*", "172.31.*",
                                "192.168.*" };

            // 创建WebProxy对象，并设置代理过滤规则
            WebProxy proxy = new WebProxy
            {
                BypassList = bypassList,
                UseDefaultCredentials = true
            };

            // 设置不使用回环地址
            proxy.BypassProxyOnLocal = true;

            try
            {
                // 将代理设置为系统默认代理
                WebRequest.DefaultWebProxy = proxy;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("代理环境设置失败：" + ex.Message);
            }
        }

        public TitaniumProxy()
        {
            //注册系统代理
            //RegisterSystemProxy();
            proxyServer = new ProxyServer();

            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;

            proxyServer.CertificateManager.CertificateValidDays = 365 * 10;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            proxyServer.CertificateManager.CertificateEngine = Titanium.Web.Proxy.Network.CertificateEngine.DefaultWindows;
            proxyServer.CertificateManager.OverwritePfxFile = false;
            proxyServer.CertificateManager.RootCertificate = GetCert();
            if (proxyServer.CertificateManager.RootCertificate == null)
            {
                Logger.PrintColor("正在进行证书安装，需要信任该证书才可进行https解密，若有提示请确定");
                proxyServer.CertificateManager.CreateRootCertificate();
            }
            proxyServer.CertificateManager.TrustRootCertificate(true);

            //https://github.com/justcoding121/titanium-web-proxy/issues/828

            proxyServer.ServerCertificateValidationCallback += ProxyServer_ServerCertificateValidationCallback;
            proxyServer.BeforeRequest += ProxyServer_BeforeRequest;
            proxyServer.BeforeResponse += ProxyServer_BeforeResponse;
            //proxyServer.AfterResponse += ProxyServer_AfterResponse;            

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, ProxyPort, true);
            explicitEndPoint.BeforeTunnelConnectRequest += ExplicitEndPoint_BeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += ExplicitEndPoint_BeforeTunnelConnectResponse;
            proxyServer.AddEndPoint(explicitEndPoint);
        }

        private X509Certificate2 GetCert()
        {
            X509Certificate2 result = proxyServer.CertificateManager.LoadRootCertificate();

            if (result != null) return result;

            return null;

            // 打开“受信任的根证书颁发机构”存储区
            using (X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);

                // 遍历证书集合
                foreach (X509Certificate2 cert in store.Certificates)
                {
                    try
                    {
                        // 尝试使用证书创建 X509Certificate2 实例
                        X509Certificate2 certificate = new X509Certificate2(cert);

                        //判断过期
                        if (DateTime.Now > certificate.NotAfter) continue;

                        //判断黑名单
                        var black = new string[] { "localhost" };
                        if (certificate.FriendlyName.ToLower().LikeIn(black) ||
                            certificate.Subject.ToLower().LikeIn(black)
                           ) continue;


                        if (certificate.FriendlyName.ToLower().LikeIn("titanium"))
                        {
                            result = certificate;
                            break;
                        }

                        // 打印证书信息
                        //Console.WriteLine("证书: " + certificate.Subject);
                        //Console.WriteLine("颁发者: " + certificate.Issuer);
                        //Console.WriteLine("有效期: " + certificate.NotBefore + " - " + certificate.NotAfter);

                        //result = certificate;
                        //break;
                    }
                    catch (CryptographicException ex)
                    {
                        //捕获加密异常，如果证书需要密码，这里会抛出异常
                        throw new Exception("证书加载失败: " + ex.Message);
                    }
                }

                store.Close();
            }

            return result;
        }

        public override void SetUpstreamProxy(string upstreamProxyAddr)
        {
            if (string.IsNullOrWhiteSpace(upstreamProxyAddr)) return;
            upstreamProxyAddr = upstreamProxyAddr.Trim();
            var reg = new Regex(@"[a-zA-Z0-9\.]+:\d+");
            if (!reg.IsMatch(upstreamProxyAddr))
            {
                throw new Exception("上游代理地址格式不正确，必须为ip:port格式");
            }
            //设置上游代理地址
            //var upstreamProxyAddr = Appsetting.Current.UpstreamProxy;
            if (!upstreamProxyAddr.IsNullOrWhiteSpace())
            {
                upStreamProxy = new ExternalProxy()
                {
                    HostName = upstreamProxyAddr.Split(':')[0],
                    Port = int.Parse(upstreamProxyAddr.Split(':')[1]),
                    ProxyType = ExternalProxyType.Http
                };
                proxyServer.UpStreamHttpProxy = upStreamProxy;
                proxyServer.UpStreamHttpsProxy = upStreamProxy;
            }
        }

        private bool CheckBrowser(string processName)
        {
            return AppSetting.Current.ProcessFilter.Contains(processName) && processName != "直播伴侣" && processName != "douyin";
        }

        /// <summary>
        /// 检测是否为快手相关进程（快手直播伴侣、快手客户端）
        /// </summary>
        private bool CheckKuaishouProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            processName = processName.ToLowerInvariant();
            // 扩展快手进程名列表
            var ksProcessNames = new[] {
                "快手直播伴侣",      // 中文名
                "kscloudtv",        // 快手直播伴侣英文
                "KSCloudTV",        
                "kuaishou",         // 快手主程序
                "快手",              // 简写
                "kuaishoupay",      // 快手支付相关
                "ksnebula",         // 快手星芒
                "gifshow",          // 快手旧名
                "KSApp",            // Mac 版快手
                "KwaiMix"           // 快手国际版
            };
            
            foreach (var name in ksProcessNames)
            {
                if (processName.Contains(name.ToLowerInvariant()))
                    return true;
            }
            
            // 也检查用户配置里与“当前进程名”相关的快手关键字
            foreach (var filter in AppSetting.Current.ProcessFilter.Select(f => (f ?? string.Empty).ToLowerInvariant()))
            {
                if (!processName.Contains(filter)) continue;
                if (filter.Contains("kuaishou") || filter.Contains("kscloud") || filter.Contains("gifshow") || filter.Contains("kwailive"))
                    return true;
            }
            
            return false;
        }

        /// <summary>
        /// 检测是否为快手弹幕相关请求
        /// </summary>
        private bool IsKuaishouBarrageRequest(string hostname, string uri)
        {
            hostname = hostname?.ToLower() ?? "";
            uri = uri?.ToLower() ?? "";
            foreach (var h in ksBarrageHosts)
            {
                if (hostname.Contains(h)) return true;
            }
            if (ksBarrageReg.IsMatch(uri)) return true;
            return false;
        }

        private Task ProxyServer_BeforeRequest(object sender, SessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string uri = e.HttpClient.Request.RequestUri.ToString();
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            TryLogKuaishouHttpRequestIndex(hostname, uri, processName, e.HttpClient.Request.Method?.ToString() ?? "UNKNOWN");

            // 只对 WS 升级请求订阅 DataReceived（防止普通HTTP请求误订阅导致解析错误）
            // WS 升级请求特征：Upgrade: websocket 头，或 ConnectRequest.TunnelType == Websocket
            bool isKwaiProcess = CheckKuaishouProcess(processName);
            bool isKuaishouDomain = IsKuaishouBarrageRequest(hostname, uri);
            bool isTunnelWs = e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket;
            var upgradeHeader = e.HttpClient.Request.Headers.GetFirstHeader("Upgrade")?.Value ?? "";
            bool isWsUpgrade = upgradeHeader.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0;

            // 快手抓包模式只跟随官方客户端进程，避免浏览器流量干扰鉴权链路
            if (isKwaiProcess && (isTunnelWs || isWsUpgrade))
            {
                e.DataReceived -= WebSocket_DataReceived;
                e.DataReceived += WebSocket_DataReceived;
                Logger.LogInfo($"[KS_REQ] 官方客户端握手捕获 hostname:{hostname} Process:{processName} isTunnelWs={isTunnelWs} isWsUpgrade={isWsUpgrade}");
            }

            return Task.CompletedTask;
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";

            // ===== 快手进程 DEBUG 日志（找 roomId/liveStreamId 用，稳定后可删除）=====
            try
            {
                bool isKwaiProcess = processName != null && (
                    processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    processName.IndexOf("kscloudtv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    processName.IndexOf("kuaishou", StringComparison.OrdinalIgnoreCase) >= 0
                );
                if (isKwaiProcess)
                {
                    var statusCode = e.HttpClient.Response.StatusCode;
                    var ct = contentType?.Split(';')[0]?.Trim() ?? "";
                    // 只记录 JSON/文本响应，跳过图片/视频等二进制
                    bool isTextLike = ct.Contains("json") || ct.Contains("text") || ct.Contains("javascript") || ct.Contains("xml");
                    if (isTextLike)
                    {
                        try
                        {
                            var body = await e.GetResponseBodyAsString();
                            var snippet = body?.Length > 800 ? body.Substring(0, 800) + "..." : body;
                            Logger.LogInfo($"[KS_DEBUG] Process={processName} Status={statusCode} URL={uri}\nBody={snippet}");
                        }
                        catch
                        {
                            Logger.LogInfo($"[KS_DEBUG] Process={processName} Status={statusCode} URL={uri} (body read failed)");
                        }
                    }
                    else
                    {
                        Logger.LogInfo($"[KS_DEBUG] Process={processName} Status={statusCode} ContentType={ct} URL={uri}");
                    }

                    TryLogKuaishouHttpRedirect(processName, hostname, uri, statusCode, e.HttpClient.Response.Headers.GetFirstHeader("Location")?.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_DEBUG] 日志异常: {ex.Message}");
            }
            // ===== END DEBUG =====

            //处理直播伴侣开播更新
            await HookSelfLive(e);

            //处理弹幕
            await HookBarrage(e);

            //处理JS注入
            await HookPageAsync(e);

            //处理脚本拦截修改
            await HookScriptAsync(e);
        }

        private void TryLogKuaishouHttpRequestIndex(string hostname, string uri, string processName, string method)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(processName) || processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) < 0) return;
                var host = (hostname ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(host) || host.Contains("wlog.gifshow.com")) return;
                var knownControlHost = IsKnownKsControlHostForIndex(host);

                var reqUri = uri ?? string.Empty;
                var path = "/";
                try
                {
                    var parsed = new Uri(reqUri);
                    path = string.IsNullOrWhiteSpace(parsed.AbsolutePath) ? "/" : parsed.AbsolutePath.ToLowerInvariant();
                }
                catch
                {
                    // ignore
                }

                var ext = Path.GetExtension(path ?? string.Empty)?.ToLowerInvariant() ?? string.Empty;
                var isStaticAsset = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".ico" || ext == ".css" || ext == ".woff" || ext == ".woff2" || ext == ".ttf" || ext == ".map";
                if (isStaticAsset && !knownControlHost) return;

                var m = (method ?? "UNKNOWN").ToUpperInvariant();
                var looksLikeApiPath = path.Contains("/api/") || path.Contains("/rest/") || path.Contains("/live/") || path.Contains("/room/") || path.Contains("author") || path.Contains("stream") || path.Contains("status") || path.Contains("token");
                if (m == "GET" && !looksLikeApiPath && !knownControlHost)
                {
                    return;
                }

                var key = $"{method}|{host}|{path}";
                var now = DateTime.Now;
                lock (ksHttpReqIndexLock)
                {
                    if (ksHttpReqIndexLastAt.TryGetValue(key, out var lastAt) && (now - lastAt).TotalSeconds < 2)
                    {
                        return;
                    }
                    ksHttpReqIndexLastAt[key] = now;
                }

                var priority = (m == "POST" || m == "PUT" || m == "PATCH" || m == "OPTIONS" || knownControlHost) ? "HIGH" : "NORMAL";
                Logger.LogInfo($"[KS_HTTP_REQ_INDEX] priority={priority} method={method} host={host} knownControl={knownControlHost} path={path} uri={reqUri}");
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_HTTP_REQ_INDEX] failed: {ex.Message}");
            }
        }

        private bool IsKnownKsControlHostForIndex(string host)
        {
            var h = (host ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(h)) return false;
            return h.Contains("report-rtc-mainapp.kuaishou.com")
                || h.Contains("apijs2.ksapisrv.com")
                || h.Contains("apijsv6.ksapisrv.com")
                || h.Contains("apijs2.gifshow.com")
                || h.Contains("apijsv6.gifshow.com")
                || h.Contains("api3.gifshow.com");
        }

        private void TryLogKuaishouHttpRedirect(string processName, string hostname, string uri, int statusCode, string location)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(processName) || processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) < 0) return;
                if (!(statusCode == 301 || statusCode == 302 || statusCode == 307 || statusCode == 308)) return;
                var host = (hostname ?? string.Empty).Trim().ToLowerInvariant();
                if (host.Contains("wlog.gifshow.com")) return;

                var decoded = DecodeUrlOnce(location ?? string.Empty);
                Logger.LogInfo($"[KS_HTTP_REDIRECT] status={statusCode} host={host} uri={uri} location={decoded}");
                if (!string.IsNullOrWhiteSpace(decoded) && decoded.IndexOf("live.kuaishou.com/u/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.LogInfo($"[KS_URL_U_HIT] channel=http_redirect host={host} process={processName} url={decoded}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_HTTP_REDIRECT] failed: {ex.Message}");
            }
        }

        private string DecodeUrlOnce(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input ?? string.Empty;
            try
            {
                return Uri.UnescapeDataString(input.Replace("+", "%20"));
            }
            catch
            {
                return input;
            }
        }

        // Hook 直播伴侣开播信息并更新
        private async Task HookSelfLive(SessionEventArgs e)
        {
            //Hook 直播伴侣直播间创建 https://webcast.amemv.com/webcast/room/create/?ac=wifi&app_name=webcast_mate&version_code=7.3.3&device_platform=windows&webcast_sdk_version=1520&resolution=1707%2A1067&os_version=10.0.22621&language=zh&aid=2079&live_id=1&channel=online&device_id=2164319493312045&iid=42200736232026&extra_first_tag_id=22&extra_second_tag_id=22093&extra_third_tag_id=22093195&extra_encoder_core=qsv&extra_codec_name=h264_qsv_ex&extra_codec_is_ex=1&extra_use_265=0&msToken=8tJ0NCHWun7wHpdPd_fd0_nlUmgRwM8sQYThkqkq4-qR00mKiJ3Wd3h05r4mm5HO_R_qA2qeTIn8qR2yjcXXoh5mmXkewUuTS4G1Yoi_D-m8EZiacZVWoDqgnqw=&X-Bogus=DFSzswVLJzC4fUiKt5a4a3JCqOA1&_signature=_02B4Z6wo00001fHp2wAAAIDCdmADbSJfNUnx6d-AABpim6xRe8bmnvrrC1Z7GWTwK8sPujtot.bkv7h8bk-nde0WvO-78H3cwglXRzZk8uHTs3ZKWlZqOqaBVjgdHIRritV.peh4bkRETofZ7f
            string uri = e.HttpClient.Request.RequestUri.ToString();
            var urix = new Uri(uri);
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var response = e.HttpClient.Response;
            if (!uri.Contains("/webcast/room/create")) return;
            if (processName != "直播伴侣") return;
            if (response.StatusCode != 200) return;
            var reponse = await e.GetResponseBodyAsString();
            RoomInfo roomInfo;
            //缓存直播间信息
            var tupe = RoomInfo.TryParseStreamPusherCreate(reponse, out roomInfo);
            var code = tupe.Item1;
            var msg = tupe.Item2;

            if (code != 0)
            {
                Logger.LogWarn($"直播伴侣开播房间资料缓存失败，原因:{msg}");
                return;
            }

            var jobj = JsonConvert.DeserializeObject<JObject>(reponse);

            var roomid = jobj["data"]?["id_str"]?.Value<string>();
            var sec_uid = jobj["data"]?["owner"]?["sec_uid"]?.Value<string>();
            var nickname = jobj["data"]?["owner"]?["nickname"]?.Value<string>();
            var displayId = jobj["data"]?["owner"]?["display_id"]?.Value<string>();

            if (roomInfo != null && !roomid.IsNullOrWhiteSpace())
            {
                roomInfo.RoomId = roomid;
                roomInfo.Title = jobj["data"]?["title"]?.Value<string>();
                Logger.LogInfo($"直播伴侣开播，开播账号:{displayId} {nickname} ，更新RoomId={roomInfo.RoomId}");
            }

            if (roomInfo != null)
            {
                AppRuntime.RoomCaches.AddRoomInfoCache(roomInfo);
            }
        }

        // Hook 弹幕
        private async Task HookBarrage(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";
            var isLiveCompan = processName == "直播伴侣";
            var isWs = e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket;

            // 记录所有 WebSocket CONNECT 请求，方便调试
            if (isWs)
            {
                var isDouyin = webcastBarrageReg.IsMatch(uri);
                var isKuaishou = IsKuaishouBarrageRequest(hostname, uri);
                Logger.LogInfo($"[WS连接] Host={hostname} URI={uri} Process={processName} 抖音={isDouyin} 快手={isKuaishou}");
            }

            // 调试：记录 wsukwai 域名的所有请求（不管是否 WS）
            if (hostname.Contains("wsukwai"))
            {
                var tunnelType = e.HttpClient.ConnectRequest?.TunnelType.ToString() ?? "null";
                Logger.LogInfo($"[wsukwai调试] isWs={isWs} TunnelType={tunnelType} URI={uri} Process={processName}");
            }

            //ws 方式 - 抖音
            if (isWs && webcastBarrageReg.IsMatch(uri))
            {
                e.DataReceived -= WebSocket_DataReceived;
                e.DataReceived += WebSocket_DataReceived;
                var urix = new Uri(uri);
                var roomid = urix.GetQueryParam("room_id");
                Logger.LogInfo($"[抖音] 订阅到新的弹幕流地址，roomid:{roomid}");
            }

            // 全量镜像模式：所有WS会话都订阅原始DataReceived
            if (isWs)
            {
                e.DataReceived -= WebSocket_DataReceived;
                e.DataReceived += WebSocket_DataReceived;
                Logger.LogInfo($"[RAW_MIRROR_WS_SUB] host={hostname} process={processName} uri={uri}");
            }

            //ws 方式 - 快手（仅当 TunnelType 是 Websocket 时才订阅，避免对普通HTTP响应误订阅）
            bool isKuaishouBarrage = IsKuaishouBarrageRequest(hostname, uri);
            if (isWs && isKuaishouBarrage && CheckKuaishouProcess(processName))
            {
                e.DataReceived -= WebSocket_DataReceived;
                e.DataReceived += WebSocket_DataReceived;
                
                var urix2 = new Uri(uri.Contains("://") ? uri : $"wss://{hostname}{uri}");
                var liveStreamId2 = urix2.GetQueryParam("liveStreamId") ?? "N/A";
                var token2 = urix2.GetQueryParam("token");
                var tokenPreview2 = !string.IsNullOrEmpty(token2) && token2.Length > 10 
                    ? token2.Substring(0, 10) + "..." 
                    : (token2 ?? "N/A");
                
                Logger.LogInfo($"[快手] 订阅DataReceived hostname:{hostname} liveStreamId:{liveStreamId2} token:{tokenPreview2}");
            }

            // 全量镜像模式：HTTP原始回包统一透传给上层做raw落盘（不限制进程/content-type）
            if (!isWs)
            {
                var ct = (contentType ?? string.Empty).ToLowerInvariant();
                var payload = await e.GetResponseBody();
                if (payload != null && payload.Length > 0)
                {
                    base.FireOnFetchResponse(new HttpResponseEventArgs()
                    {
                        HttpClient = e.HttpClient,
                        ProcessID = processid,
                        HostName = hostname,
                        RequestUri = uri,
                        ProcessName = base.GetProcessName(processid),
                        Payload = payload
                    });
                    Logger.LogInfo($"[RAW_MIRROR_HTTP_FORWARD] host={hostname} process={processName} ct={ct} len={payload.Length} uri={uri}");
                }
            }

            //轮询方式(当抖音ws连接断开后，客户端也会降级使用轮询模式获取弹幕)
            if (uri.Contains(BARRAGE_POOL_PATH) && contentType.Contains("application/protobuffer"))
            {
                var payload = await e.GetResponseBody();

                var referrer = e.HttpClient.Request.Headers.GetFirstHeader("Referer")?.Value;
                //https://live.douyin.com/22404217360

                //检查webroomid映射
                if (!referrer.IsNullOrWhiteSpace())
                {
                    var webroomid = Regex.Match(referrer, @"(?<=live\.douyin\.com\/)\d+").Value;
                    var urix = new Uri(uri);
                    var roomid = urix.GetQueryParam("room_id");
                    var cookie = e.HttpClient.Request.Headers.GetFirstHeader("Cookie")?.Value;

                    var roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(roomid);
                    if (roomInfo == null && cookie != null && !rinfoRequestings.Contains(webroomid))
                    {
                        lock (rinfoRequestings) rinfoRequestings.Add(webroomid);
                        //查询后会自动缓存
                        DyApiHelper.GetRoomInfoForApi(webroomid, cookie).ContinueWith(t =>
                        {
                            var rinfo = t.Result;
                            //限制只尝试一次
                            if (rinfo != null)
                            {
                                lock (rinfoRequestings) rinfoRequestings.Remove(webroomid);
                            }
                        });
                    }

                    AppRuntime.RoomCaches.SetRoomCache(roomid, webroomid);
                }

                base.FireOnFetchResponse(new HttpResponseEventArgs()
                {
                    HttpClient = e.HttpClient,
                    ProcessID = processid,
                    HostName = hostname,
                    RequestUri = uri,
                    ProcessName = base.GetProcessName(processid),
                    Payload = payload
                });
            }
        }

        // Hook 直播页面
        private async Task HookPageAsync(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string url = e.HttpClient.Request.Url;
            var urlNoQuery = url.Split('?')[0];
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var liveRoomMactch = Regex.Match(uri.Trim(), @".*:\/\/live.douyin\.com\/([0-9a-zA-Z_]+)");
            var liveHomeMatch = Regex.Match(uri.Trim(), @".*:\/\/live.douyin\.com\/?$");
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //检测是否为dom页，用于脚本注入
            if (!contentType.Contains("text/html")) return;

            //如果响应头含有 CSP(https://blog.csdn.net/qq_30436011/article/details/127485927 会阻止内嵌脚本执行) 则删除
            var csp = e.HttpClient.Response.Headers.GetFirstHeader("Content-Security-Policy");
            if (csp != null)
            {
                e.HttpClient.Response.Headers.RemoveHeader("Content-Security-Policy");
            }

            //获取 content-type                
            if (liveRoomMactch.Success)
            {
                string webrid = liveRoomMactch.Groups[1].Value;
                //获取直播页注入js
                string liveRoomInjectScript = EmbResource.GetFileContent("livePage.js");
                //注入上下文变量;
                var scriptContext = BuildContext(new Dictionary<string, string>()
                {
                    {"PROCESS_NAME","'{processName}'"},
                    {"AUTOPAUSE",AppSetting.Current.AutoPause.ToString().ToLower()}
                });
                liveRoomInjectScript = scriptContext + liveRoomInjectScript;
                var html = await e.GetResponseBodyAsString();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                if (!liveRoomInjectScript.IsNullOrWhiteSpace())
                {
                    //利用 HtmlAgilityPack 在尾部注入script 标签
                    RoomInfo roominfo;
                    var tup = RoomInfo.TryParseRoomPageHtml(html, out roominfo);
                    int code = tup.Item1;
                    string msg = tup.Item2;

                    if (code == 0)
                    {
                        Logger.LogInfo($"直播页{webrid} [{roominfo.Owner.Nickname}]的直播间，房间信息已采集到缓存");
                        roominfo.WebRoomId = webrid;
                        roominfo.LiveUrl = url;
                        AppRuntime.RoomCaches.AddRoomInfoCache(roominfo);
                    }
                    else
                    {
                        roominfo = new RoomInfo();
                        roominfo.WebRoomId = webrid;
                        roominfo.LiveUrl = url;
                        //正则匹配主播标题
                        //<div class="st8eGKi4" data-e2e="live-room-nickname">和平精英小夜y</div>
                        var match = Regex.Match(html, @"(?<=live-room-nickname""\>).+(?=<\/div>)");
                        if (match.Success)
                        {
                            roominfo.Owner = new RoomInfo.RoomAnchor()
                            {
                                Nickname = match.Value,
                                UserId = "-1"
                            };
                        }
                        AppRuntime.RoomCaches.AddRoomInfoCache(roominfo);
                    }

                    try
                    {

                        //找到body标签,在尾部注入script标签
                        var body = doc.DocumentNode.SelectSingleNode("//body");
                        if (body != null)
                        {
                            var script = doc.CreateElement("script");
                            script.InnerHtml = liveRoomInjectScript;
                            body.AppendChild(script);
                            html = doc.DocumentNode.OuterHtml;
                            Logger.LogTrace($"直播页{urlNoQuery},用户脚本已成功注入!\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, $"直播页{url},用户脚本注入异常");
                    }
                }

                //给script标签 src加上时间戳避免缓存
                if (AppSetting.Current.DisableLivePageScriptCache)
                {
                    ScriptAddTocks(doc);
                }

                html = doc.DocumentNode.OuterHtml;

                e.SetResponseBodyString(html);
            }

            //直播主页
            if (liveHomeMatch.Success)
            {
                //获取直播页注入js
                string liveHoomInjectScript = EmbResource.GetFileContent("livePage.js");

                //注入上下文变量;
                var scriptContext = BuildContext(new Dictionary<string, string>()
                {
                    {"PROCESS_NAME","'{processName}'"},
                    {"AUTOPAUSE",AppSetting.Current.AutoPause.ToString().ToLower()}
                });
                liveHoomInjectScript = scriptContext + liveHoomInjectScript;


                if (!liveHoomInjectScript.IsNullOrWhiteSpace())
                {
                    //利用 HtmlAgilityPack 在尾部注入script 标签
                    var html = await e.GetResponseBodyAsString();
                    var doc = new HtmlAgilityPack.HtmlDocument();
                    doc.LoadHtml(html);
                    //找到body标签,在尾部注入script标签
                    var body = doc.DocumentNode.SelectSingleNode("//body");
                    if (body != null)
                    {
                        var script = doc.CreateElement("script");
                        script.InnerHtml = liveHoomInjectScript;
                        body.AppendChild(script);
                        var newHtml = doc.DocumentNode.OuterHtml;
                        e.SetResponseBodyString(newHtml);
                        Logger.PrintColor($"直播首页{urlNoQuery},用户脚本已成功注入!\n", ConsoleColor.Green);
                    }
                }
            }
        }

        //给部分脚本加上时间戳避免缓存
        private void ScriptAddTocks(HtmlDocument doc)
        {
            var scripts = doc.DocumentNode.SelectNodes("//script[@src]");
            if (scripts != null)
            {
                var ticks = DateTime.Now.Ticks;
                foreach (var script in scripts)
                {
                    var src = script.Attributes["src"].Value;

                    var srcUri = new Uri(src);
                    if (!CheckHost(srcUri.Host)) continue;

                    var fileName = Path.GetFileName(src.Split('?')[0]);
                    //目前只需要用到相关这些js
                    if (!fileName.StartsWith("island") && !src.Contains(LIVE_SCRIPT_PATH)) continue;

                    if (src.Contains("?"))
                    {
                        src += "&_t=" + ticks;
                    }
                    else
                    {
                        src += "?_t=" + ticks;
                    }
                    script.Attributes["src"].Value = src;
                }

            }
        }

        //生成注入上下文
        private string BuildContext(IDictionary<string, string> constVals)
        {
            var scriptContext = string.Join("\r\n", constVals.Select(s =>
            {
                return "const " + s.Key + " = " + s.Value + ";";
            }));
            return scriptContext;
        }

        // Hook Script 脚本
        private async Task HookScriptAsync(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string url = e.HttpClient.Request.Url;
            var urlNoQuery = url.Split('?')[0];
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";
            var fileName = Path.GetFileName(urlNoQuery);

            if (contentType == null) return;
            if (!contentType.Trim().ToLower().Contains("application/javascript")) return;
            if (e.HttpClient.Response.StatusCode != 200) return;

            //https://lf-webcast-platform.bytetos.com/obj/webcast-platform-cdn/webcast/douyin_live/chunks/island_a74ce.b55095a0.js
            //判断响应内容是否为js application/javascript
            if (processName != "直播伴侣" && processName != "douyin"
                && fileName.StartsWith("island")
                )
            {
                var js = await e.GetResponseBodyAsString();
                var reg = new Regex(@"if\(!\(\d{1,},\w{1,}\.DJ\)\(\).+\w{1,}\.includes\(""live""\)\)\)\{");
                var match = reg.Match(js);
                if (match.Success)
                {
                    js = reg.Replace(js, "if(false){");
                    e.SetResponseBodyString(js);
                    Logger.PrintColor($"已成功绕过JS页面无操作检测 {urlNoQuery}\n", ConsoleColor.Green);
                }
            }

            if (url.Contains(LIVE_SCRIPT_PATH) && AppSetting.Current.ForcePolling)
            {
                var reg = new Regex(@"if\s*\((?<patt>!this\.stopPolling)\)");
                var js = await e.GetResponseBodyAsString();
                var match = reg.Match(js);
                if (match.Success)
                {
                    var pollingIntervalReg = new Regex(@"this\.errorInterval\s*=(?<value>.+?),");
                    var pollingIntervalMatch = pollingIntervalReg.Match(js);
                    if (pollingIntervalMatch.Success)
                    {
                        var myValue = AppSetting.Current.PollingInterval;
                        js = pollingIntervalReg.Replace(js, $"this.pollingInterval={myValue},");
                        Logger.PrintColor($"直播间已成功修改轮询间隔为{myValue}ms", ConsoleColor.Green);
                    }

                    js = reg.Replace(js, "if(true)");
                    e.SetResponseBodyString(js);
                    Logger.PrintColor($"直播间已强制启用Http轮询模式", ConsoleColor.Green);
                }
            }
        }

        private Task ProxyServer_ServerCertificateValidationCallback(object sender, CertificateValidationEventArgs e)
        {
            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }
            return Task.CompletedTask;
        }

        //控制要解密SSL的域名
        private async Task ExplicitEndPoint_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string url = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);

            // 只对已知直播客户端进程做 SSL 解密，其他进程（游戏App、浏览器等）直接透传
            var knownLiveProcesses = new[] { "直播伴侣", "kwailive", "webcast_mate", "douyin" };
            bool isLiveProcess = knownLiveProcesses.Any(p => processName != null && processName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

            // DEBUG：kwailive 进程解密所有 HTTPS（包括 IP 直连），方便找到开播 API（稳定后改回 CheckHost）
            bool isKwaiDebug = processName != null && processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) >= 0;
            e.DecryptSsl = isKwaiDebug ? true : (isLiveProcess && CheckHost(hostname));

            Logger.LogInfo($"[CONNECT] Host={hostname} DecryptSsl={e.DecryptSsl} Process={processName}");
        }

        // 隧道建立后，对 kwailive 进程订阅 DecryptedDataReceived（含IP直连的弹幕WS）
        private Task ExplicitEndPoint_BeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);

            bool isKwaiProcess = CheckKuaishouProcess(processName);
            bool isKwaiObserveProcess = IsKuaishouObserveProcess(processName);

            if (isKwaiObserveProcess)
            {
                // 重置该隧道的解析状态，避免复用旧缓存
                _ksTunnelBuffers[e] = new List<byte>();
                _ksTunnelHandshakeDone[e] = false;
                TouchTunnelState(e);
                e.DataReceived -= TunnelRawDataReceived;
                e.DataReceived += TunnelRawDataReceived;
                e.DataSent -= TunnelRawDataSent;
                e.DataSent += TunnelRawDataSent;
                if (isKwaiProcess)
                {
                    e.DecryptedDataReceived -= TunnelDecryptedDataReceived;
                    e.DecryptedDataReceived += TunnelDecryptedDataReceived;
                }
                Logger.LogInfo($"[KS_TUNNEL] 官方客户端隧道捕获 hostname:{hostname} Process:{processName} decodeWs={isKwaiProcess}");
            }

            return Task.CompletedTask;
        }

        // 为每个 kwailive 隧道连接维护未处理完的字节 buffer
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TunnelConnectSessionEventArgs, List<byte>> _ksTunnelBuffers
            = new System.Collections.Concurrent.ConcurrentDictionary<TunnelConnectSessionEventArgs, List<byte>>();
        // 标记该隧道是否已跳过 HTTP 升级握手头
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TunnelConnectSessionEventArgs, bool> _ksTunnelHandshakeDone
            = new System.Collections.Concurrent.ConcurrentDictionary<TunnelConnectSessionEventArgs, bool>();
        // 记录隧道最后活跃时间，用于回收长期不活跃连接，防止缓存增长
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TunnelConnectSessionEventArgs, DateTime> _ksTunnelLastSeenUtc
            = new System.Collections.Concurrent.ConcurrentDictionary<TunnelConnectSessionEventArgs, DateTime>();
        // 快手流量画像（用于定位真实评论主通道）
        private sealed class KsFlowProfile
        {
            public long RxPackets;
            public long RxBytes;
            public long TxPackets;
            public long TxBytes;
            public long RxLargePackets;
            public int MaxRx;
            public int MaxTx;
            public DateTime LastSeenUtc;
        }
        private readonly ConcurrentDictionary<string, KsFlowProfile> _ksFlowProfiles = new ConcurrentDictionary<string, KsFlowProfile>();
        private DateTime _ksFlowLastEmitUtc = DateTime.MinValue;
        private readonly object _ksFlowEmitLock = new object();
        private volatile bool _ksBusinessSeenRecently = false;
        private DateTime _ksBusinessSeenAtUtc = DateTime.MinValue;

        private bool IsKuaishouObserveProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            return processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) >= 0
                || processName.IndexOf("kwaiwebview", StringComparison.OrdinalIgnoreCase) >= 0
                || CheckKuaishouProcess(processName);
        }

        // 处理隧道解密后的原始数据（用于 IP 直连的快手弹幕 WS）
        private void TunnelDecryptedDataReceived(object sender, DataEventArgs e)
        {
            var args = sender as TunnelConnectSessionEventArgs;
            if (args == null) return;
            TouchTunnelState(args);

            string hostname = args.HttpClient.Request.RequestUri.Host;
            var processid = args.HttpClient.ProcessId.Value;

            try
            {
                var buf = _ksTunnelBuffers.GetOrAdd(args, _ => new List<byte>());
                if (e.Count > 0)
                {
                    var first = e.Buffer[e.Offset];
                    Logger.LogInfo($"[KS_TUNNEL_RAW] host:{hostname} process:{base.GetProcessName(processid)} recvCount:{e.Count} firstByte:0x{first:X2}");
                }
                buf.AddRange(new ArraySegment<byte>(e.Buffer, e.Offset, e.Count));

                // 跳过 HTTP 升级握手（GET ... / HTTP/1.1 101 ...）
                bool handshakeDone = _ksTunnelHandshakeDone.GetOrAdd(args, _ => false);
                if (!handshakeDone)
                {
                    var bufArr = buf.ToArray();
                    // 查找 \r\n\r\n（HTTP 头结束标志）
                    int headerEnd = -1;
                    for (int i = 0; i <= bufArr.Length - 4; i++)
                    {
                        if (bufArr[i] == 0x0D && bufArr[i+1] == 0x0A && bufArr[i+2] == 0x0D && bufArr[i+3] == 0x0A)
                        {
                            headerEnd = i + 4;
                            break;
                        }
                    }
                    if (headerEnd < 0)
                    {
                        // 还没收到完整的 HTTP 头，等待更多数据
                        // 但如果前两字节不像 HTTP（不是 'G','P','H' 等），说明不是 HTTP 握手直接是 WS 帧
                        if (bufArr.Length >= 1 && bufArr[0] != 0x47 && bufArr[0] != 0x48 && bufArr[0] != 0x50)
                        {
                            // 直接是 WS 帧，无握手
                            _ksTunnelHandshakeDone[args] = true;
                            handshakeDone = true;
                            Logger.LogInfo($"[KS_TUNNEL] 识别为直接WS帧(无HTTP握手) hostname:{hostname} firstByte:0x{bufArr[0]:X2} bufferLen:{bufArr.Length}");
                        }
                        else
                        {
                            if (bufArr.Length > 0)
                            {
                                Logger.LogInfo($"[KS_TUNNEL] 等待HTTP握手结束 hostname:{hostname} firstByte:0x{bufArr[0]:X2} bufferLen:{bufArr.Length}");
                            }
                            return; // 等待完整 HTTP 头
                        }
                    }
                    else
                    {
                        buf.RemoveRange(0, headerEnd);
                        _ksTunnelHandshakeDone[args] = true;
                        handshakeDone = true;
                        Logger.LogInfo($"[KS_TUNNEL] 跳过HTTP握手 {headerEnd}字节 hostname:{hostname} 剩余:{buf.Count}字节");
                    }
                }

                while (buf.Count >= 2)
                {
                    var data = buf.ToArray();
                    int idx = 0;
                    byte b0 = data[idx++];
                    byte b1 = data[idx++];
                    bool masked = (b1 & 0x80) != 0;
                    int opcode = b0 & 0x0F;
                    long payloadLen = b1 & 0x7F;

                    if (payloadLen == 126)
                    {
                        if (data.Length < idx + 2) break;
                        payloadLen = (data[idx] << 8) | data[idx + 1];
                        idx += 2;
                    }
                    else if (payloadLen == 127)
                    {
                        if (data.Length < idx + 8) break;
                        payloadLen = 0;
                        for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | data[idx + i];
                        idx += 8;
                    }

                    int maskLen = masked ? 4 : 0;
                    long totalLen = idx + maskLen + payloadLen;
                    if (data.Length < totalLen) break;

                    byte[] payload = new byte[payloadLen];
                    if (masked)
                    {
                        byte[] mask = new byte[] { data[idx], data[idx + 1], data[idx + 2], data[idx + 3] };
                        idx += 4;
                        for (long i = 0; i < payloadLen; i++)
                            payload[i] = (byte)(data[idx + i] ^ mask[i % 4]);
                    }
                    else
                    {
                        Array.Copy(data, idx, payload, 0, payloadLen);
                    }

                    buf.RemoveRange(0, (int)totalLen);

                    // opcode: 0=continuation, 1=text, 2=binary, 8=close, 9=ping, 10=pong
                    if (opcode == 1 || opcode == 2)
                    {
                        if (payload.Length > 0)
                        {
                            Logger.LogInfo($"[KS_TUNNEL] 收到WS帧 hostname:{hostname} opcode:{opcode} size:{payload.Length}");
                            base.FireWsEvent(new WsMessageEventArgs()
                            {
                                ProcessID = processid,
                                HostName = hostname,
                                Payload = payload,
                                ProcessName = base.GetProcessName(processid)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_TUNNEL] 解析帧出错: {ex.Message}");
                _ksTunnelBuffers.TryRemove(args, out _);
                _ksTunnelHandshakeDone.TryRemove(args, out _);
                _ksTunnelLastSeenUtc.TryRemove(args, out _);
            }
        }

        // 处理隧道原始密文数据（用于判断数据流真实走向）
        private void TunnelRawDataReceived(object sender, DataEventArgs e)
        {
            var args = sender as TunnelConnectSessionEventArgs;
            if (args == null || e.Count <= 0) return;
            TouchTunnelState(args);
            var host = args.HttpClient.Request.RequestUri.Host;
            var processId = args.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processId);
            var first = e.Buffer[e.Offset];
            bool isKwaiProcess = !string.IsNullOrWhiteSpace(processName) && processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isKwaiProcess && IsLikelyKsHeartbeatPacket(e.Buffer, e.Offset, e.Count))
            {
                // 心跳包不参与后续解析，避免日志噪音和误判
                return;
            }
            bool isWlogHost = !string.IsNullOrWhiteSpace(host) && host.IndexOf("wlog.gifshow.com", StringComparison.OrdinalIgnoreCase) >= 0;
            bool looksBusiness = e.Count >= 200 && first != 0x16 && first != 0x14;
            if (looksBusiness)
            {
                _ksBusinessSeenRecently = true;
                _ksBusinessSeenAtUtc = DateTime.UtcNow;
            }
            bool shouldVerbose = _ksBusinessSeenRecently && (DateTime.UtcNow - _ksBusinessSeenAtUtc).TotalMinutes <= 3;
            if (!isWlogHost && (shouldVerbose || looksBusiness))
            {
                Logger.LogInfo($"[KS_TUNNEL_RAW_RX] host:{host} process:{base.GetProcessName(processId)} recvCount:{e.Count} firstByte:0x{first:X2}");
            }
            if (IsKuaishouObserveProcess(processName))
            {
                UpdateKsFlowProfile(host, processName, e.Count, isRx: true);
            }

            // 对快手直播伴侣的“非标准WS帧”通道做透传，交给上层做协议探测解析
            // 0x16/0x17 多为 TLS 握手/应用层密文，跳过避免噪音；其余字节流尝试上送
            bool looksTlsRecord = first == 0x16 || first == 0x17 || first == 0x14;
            if (isKwaiProcess && !looksTlsRecord && e.Count >= 20)
            {
                var payload = new byte[e.Count];
                Buffer.BlockCopy(e.Buffer, e.Offset, payload, 0, e.Count);
                Logger.LogInfo($"[KS_TUNNEL_RAW_FORWARD] host:{host} size:{payload.Length} firstByte:0x{first:X2}");
                base.FireWsEvent(new WsMessageEventArgs()
                {
                    ProcessID = processId,
                    HostName = "ksraw:" + host,
                    Payload = payload,
                    ProcessName = processName,
                    NeedDecompress = false
                });
            }
        }

        // 处理隧道原始上行密文数据（客户端->服务端）
        private void TunnelRawDataSent(object sender, DataEventArgs e)
        {
            var args = sender as TunnelConnectSessionEventArgs;
            if (args == null || e.Count <= 0) return;
            TouchTunnelState(args);
            var host = args.HttpClient.Request.RequestUri.Host;
            var processId = args.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processId);
            if (!string.IsNullOrWhiteSpace(processName)
                && processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) >= 0
                && e.Count == 27 && e.Buffer[e.Offset] == 0x01)
            {
                // 上行 27 字节固定心跳包，忽略日志
                return;
            }
            var first = e.Buffer[e.Offset];
            bool isWlogHost = !string.IsNullOrWhiteSpace(host) && host.IndexOf("wlog.gifshow.com", StringComparison.OrdinalIgnoreCase) >= 0;
            bool shouldVerbose = _ksBusinessSeenRecently && (DateTime.UtcNow - _ksBusinessSeenAtUtc).TotalMinutes <= 3;
            if (!isWlogHost && shouldVerbose)
            {
                Logger.LogInfo($"[KS_TUNNEL_RAW_TX] host:{host} process:{base.GetProcessName(processId)} sendCount:{e.Count} firstByte:0x{first:X2}");
            }
            if (IsKuaishouObserveProcess(processName))
            {
                UpdateKsFlowProfile(host, processName, e.Count, isRx: false);
            }
        }

        private bool IsLikelyKsHeartbeatPacket(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count < 20) return false;
            // 当前观测到的快手下行心跳包特征：固定前导 + 小包长度（常见 43）
            if (count > 90) return false;
            if (buffer[offset] != 0x01) return false;
            if (buffer[offset + 1] != 0x1A) return false;
            if (buffer[offset + 2] != 0x2B) return false;
            if (buffer[offset + 3] != 0x3C) return false;
            return true;
        }

        private void UpdateKsFlowProfile(string host, string processName, int size, bool isRx)
        {
            var key = $"{(processName ?? "unknown").ToLowerInvariant()}@{(host ?? "unknown").ToLowerInvariant()}";
            _ksFlowProfiles.AddOrUpdate(key,
                _ => new KsFlowProfile
                {
                    RxPackets = isRx ? 1 : 0,
                    RxBytes = isRx ? size : 0,
                    TxPackets = isRx ? 0 : 1,
                    TxBytes = isRx ? 0 : size,
                    RxLargePackets = (isRx && size >= 200) ? 1 : 0,
                    MaxRx = isRx ? size : 0,
                    MaxTx = isRx ? 0 : size,
                    LastSeenUtc = DateTime.UtcNow
                },
                (_, old) =>
                {
                    if (isRx)
                    {
                        old.RxPackets++;
                        old.RxBytes += size;
                        if (size >= 200) old.RxLargePackets++;
                        if (size > old.MaxRx) old.MaxRx = size;
                    }
                    else
                    {
                        old.TxPackets++;
                        old.TxBytes += size;
                        if (size > old.MaxTx) old.MaxTx = size;
                    }
                    old.LastSeenUtc = DateTime.UtcNow;
                    return old;
                });

            TryEmitKsFlowProfile();
        }

        private void TryEmitKsFlowProfile()
        {
            var now = DateTime.UtcNow;
            lock (_ksFlowEmitLock)
            {
                if ((now - _ksFlowLastEmitUtc).TotalSeconds < 20) return;
                _ksFlowLastEmitUtc = now;
            }

            var hot = _ksFlowProfiles
                .Where(kv => (now - kv.Value.LastSeenUtc).TotalMinutes <= 3)
                .OrderByDescending(kv => kv.Value.RxBytes + kv.Value.TxBytes)
                .Take(8)
                .ToList();
            if (!hot.Any()) return;
            if (!_ksBusinessSeenRecently || (now - _ksBusinessSeenAtUtc).TotalMinutes > 3) return;
            if (!hot.Any(kv => kv.Value.RxLargePackets > 0 && !kv.Key.Contains("@wlog.gifshow.com"))) return;

            Logger.LogInfo("[KS_FLOW] ===== 快手流量画像 Top =====");
            foreach (var kv in hot)
            {
                var key = kv.Key; // process@host
                var v = kv.Value;
                var host = key.Contains("@") ? key.Split('@')[1] : key;
                var candidate = v.RxLargePackets > 0
                                && !host.Contains("wlog.")
                                && !host.Contains("log-sdk")
                                && !host.Contains("ksapisrv");
                Logger.LogInfo($"[KS_FLOW] {key} rxPkt={v.RxPackets} rxBytes={v.RxBytes} txPkt={v.TxPackets} txBytes={v.TxBytes} rxLarge={v.RxLargePackets} maxRx={v.MaxRx} candidate={candidate}");
            }
        }

        private void TouchTunnelState(TunnelConnectSessionEventArgs args)
        {
            _ksTunnelLastSeenUtc[args] = DateTime.UtcNow;
            if (_ksTunnelLastSeenUtc.Count > 256)
            {
                CleanupStaleTunnelState();
            }
        }

        private void CleanupStaleTunnelState()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _ksTunnelLastSeenUtc)
            {
                if ((now - kv.Value).TotalMinutes <= 10) continue;
                var key = kv.Key;
                _ksTunnelLastSeenUtc.TryRemove(key, out _);
                _ksTunnelBuffers.TryRemove(key, out _);
                _ksTunnelHandshakeDone.TryRemove(key, out _);
            }
        }

        //检测域名白名单
        protected override bool CheckHost(string hostname)
        {
            //需要解析SSL的域名 放在这里，全开会导致性能问题，应只解析业务需要的域名
            var decryptSsls = new string[]
            {
                SCRIPT_HOST,
                LIVE_HOST,
                WEBCAST_AMEMV_HOST , //直播伴侣开播请求地址
                "*-webcast-platform.bytetos.com", //新的脚本地址
                "*webcast*", //所有带webcast的域名

                // ---- 快手直播域名（精确匹配，避免宽泛通配符导致 Titanium 崩溃）----
                "live-ws-group.kuaishou.com",
                "live-ws.kuaishou.com",
                "live-ws-pg-group1.kuaishou.com",
                "live-ws-pg-group2.kuaishou.com",
                "live-ws-pg-group3.kuaishou.com",
                "live-ws-pg-group4.kuaishou.com",
                "live-ws-pg-group5.kuaishou.com",
                "livejs-ws.kuaishou.cn",
                "p3-live.wsukwai.com",   // 快手直播伴侣弹幕WS域名
            };

            if (decryptSsls.Contains(hostname))
            {
                return true;
            }

            if (hostname.WildcardMatchAny(decryptSsls))
            {
                return true;
            }

            hostname = hostname.Trim().ToLower();
            return base.CheckHost(hostname);
        }

        //WebSocket 流读取
        private async void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = sender as SessionEventArgs;
            if (args == null) return; // TunnelConnectSessionEventArgs 不支持，跳过

            string hostname = args.HttpClient.Request.RequestUri.Host;

            var processid = args.HttpClient.ProcessId.Value;

            List<byte> messageData = new List<byte>();

            try
            {
                foreach (var frame in args.WebSocketDecoderReceive.Decode(e.Buffer, e.Offset, e.Count))
                {
                    if (frame.OpCode == WebsocketOpCode.Continuation)
                    {
                        messageData.AddRange(frame.Data.ToArray());
                        continue;
                    }
                    else
                    {
                        //读取完毕
                        byte[] payload;
                        if (messageData.Count > 0)
                        {
                            messageData.AddRange(frame.Data.ToArray());
                            payload = messageData.ToArray();
                            messageData.Clear();
                        }
                        else
                        {
                            payload = frame.Data.ToArray();
                        }

                        base.FireWsEvent(new WsMessageEventArgs()
                        {
                            ProcessID = processid,
                            HostName = hostname,
                            Payload = payload,
                            ProcessName = base.GetProcessName(processid)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.PrintColor("解析某个WebSocket包出错：" + ex.Message);
            }

            if (messageData.Count > 0)
            {
                // 没有收到 WebSocket 帧的结束帧，抛出异常或者进行处理
            }

        }


        /// <summary>
        /// 释放资源，关闭系统代理
        /// </summary>
        override public void Dispose()
        {
            proxyServer.Stop();
            proxyServer.Dispose();
            if (AppSetting.Current.UsedProxy)
            {
                CloseSystemProxy();
            }
        }

        /// <summary>
        /// 启动监听
        /// </summary>
        override public void Start()
        {
            proxyServer.Start(false);

            if (AppSetting.Current.UsedProxy)
            {
                base.RegisterSystemProxy();
                Logger.LogInfo($"系统代理代理已启动，127.0.0.1:{base.ProxyPort}");
                //使用其自带的系统代理设置可能会导致格式问题
                //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
                //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            }
            else
            {
                Logger.LogInfo($"代理已启动(局域代理)，127.0.0.1:{base.ProxyPort}");
            }
        }
    }
}
