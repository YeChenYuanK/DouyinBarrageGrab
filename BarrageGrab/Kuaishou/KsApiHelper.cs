using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BarrageGrab.Kuaishou
{
    /// <summary>
    /// 快手直播间信息
    /// </summary>
    public class KsRoomInfo
    {
        /// <summary>
        /// 直播间 liveStreamId（WebSocket 连接用）
        /// </summary>
        public string LiveStreamId { get; set; }

        /// <summary>
        /// 主播 EId（用户ID的字符串形式）
        /// </summary>
        public string AuthorId { get; set; }

        /// <summary>
        /// 主播昵称
        /// </summary>
        public string AuthorName { get; set; }

        /// <summary>
        /// 直播标题
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 封面图URL
        /// </summary>
        public string CoverUrl { get; set; }

        /// <summary>
        /// WebSocket 连接 token（部分接口返回，可选）
        /// </summary>
        public string Token { get; set; }

        /// <summary>
        /// WebSocket 服务地址列表
        /// </summary>
        public List<string> WebSocketUrls { get; set; } = new List<string>();

        /// <summary>
        /// 房间号（URL中的 userId 或短链）
        /// </summary>
        public string RoomId { get; set; }

        /// <summary>
        /// 是否正在直播
        /// </summary>
        public bool IsLive { get; set; }
    }

    /// <summary>
    /// 快手 API 辅助类：负责获取直播间信息、WebSocket 连接参数
    /// 使用 HttpWebRequest（.NET 内置），无需 NuGet System.Net.Http 包
    /// </summary>
    public class KsApiHelper
    {
        // ---- 常量 ----
        const string MOBILE_UA = "Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) " +
                                  "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1";

        const string PC_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                              "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        // 快手移动端直播页面（rid = 主播的快手号/短ID）
        const string KS_MOBILE_LIVE_URL = "https://m.gifshow.com/fw/live/{0}";

        // 快手 PC 直播页面
        const string KS_PC_LIVE_URL = "https://live.kuaishou.com/u/{0}";

        // 快手直播伴侣 WebSocket 弹幕服务器 Host（用于 TitaniumProxy 白名单）
        public const string KS_DANMU_WS_HOST = "live-ws-group.kuaishou.com";

        // 快手弹幕 WebSocket 路径（新版本使用 /websocket）
        public const string KS_DANMU_WS_PATH = "/websocket";

        // 旧版路径（保留作为备用）
        public const string KS_DANMU_WS_PATH_LEGACY = "/api/kuaishou/live/web/im/init";

        // 快手弹幕心跳间隔（毫秒）
        public const int KS_HEARTBEAT_INTERVAL_MS = 20000;

        // ---- 私有字段 ----
        private static string _cookie = "";
        private const int TIMEOUT_MS = 15000;

        public KsApiHelper()
        {
            // 关闭全局证书验证（兼容自签名证书）
            ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        }

        /// <summary>
        /// 设置全局 Cookie（从浏览器复制，提升稳定性）
        /// </summary>
        public static void SetCookie(string cookie) => _cookie = cookie ?? "";

        /// <summary>
        /// 通过快手主播的短ID/用户ID 获取直播间信息及 WebSocket 连接参数
        /// </summary>
        public async Task<KsRoomInfo> GetRoomInfoAsync(string userId)
        {
            var info = await TryGetFromMobilePage(userId);
            if (info != null && info.IsLive) return info;
            info = await TryGetFromPcPage(userId);
            return info;
        }

        // -------------------- 移动端页面解析 --------------------
        private async Task<KsRoomInfo> TryGetFromMobilePage(string userId)
        {
            try
            {
                var url = string.Format(KS_MOBILE_LIVE_URL, userId);
                var html = await GetHtmlAsync(url, MOBILE_UA);
                if (string.IsNullOrWhiteSpace(html)) return null;

                var info = new KsRoomInfo { RoomId = userId };

                JObject liveStream = null;

                // 方法1：标准匹配
                var m = Regex.Match(html, @"""liveStream"":(\{.*?\})"",""?obfuseData", RegexOptions.Singleline);
                if (m.Success)
                {
                    try { liveStream = JObject.Parse(m.Groups[1].Value); } catch { }
                }

                // 方法2：window.__INITIAL_STATE__
                if (liveStream == null)
                {
                    var sm = Regex.Match(html, @"window\.__INITIAL_STATE__\s*=\s*({.+?})\s*;?\s*</script>", RegexOptions.Singleline);
                    if (sm.Success)
                    {
                        try
                        {
                            var state = JObject.Parse(sm.Groups[1].Value);
                            liveStream = state.SelectToken("$.liveStream") as JObject;
                        }
                        catch { }
                    }
                }

                if (liveStream == null)
                {
                    Logger.LogWarn("[KS] 移动端页面未找到 liveStream 数据块");
                    if (html.Contains("liveStream"))
                        Logger.LogInfo("[KS] 页面包含 liveStream 关键字，但正则匹配失败");
                    Logger.LogInfo("[KS] 移动端页面内容(前2000字符): " + (html.Length > 2000 ? html.Substring(0, 2000) : html));
                    return null;
                }

                info.LiveStreamId = liveStream["liveStreamId"]?.Value<string>() ?? "";
                info.AuthorId = liveStream["authorId"]?.Value<string>() ?? "";
                info.AuthorName = liveStream["author"]?["name"]?.Value<string>() ?? "";
                info.Title = liveStream["caption"]?.Value<string>() ?? "";
                info.CoverUrl = liveStream["coverUrls"]?.FirstOrDefault()?.Value<string>() ?? "";

                Logger.LogInfo($"[KS] 移动端页面解析成功: liveStreamId={info.LiveStreamId}, authorId={info.AuthorId}, authorName={info.AuthorName}");

                var wsInfo = await GetDanmuWsInfo(info.LiveStreamId, info.AuthorId, html);
                if (wsInfo != null)
                {
                    info.Token = wsInfo.Item1;
                    info.WebSocketUrls = wsInfo.Item2;
                    Logger.LogInfo($"[KS] WS信息: token长度={info.Token?.Length ?? 0}, wsUrls数量={info.WebSocketUrls?.Count ?? 0}");
                    if (info.WebSocketUrls != null)
                        foreach (var wu in info.WebSocketUrls)
                            Logger.LogInfo($"[KS]   wsUrl: {wu}");
                }

                info.IsLive = !string.IsNullOrWhiteSpace(info.LiveStreamId);
                return info;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 移动端页面解析失败: " + ex.Message);
                return null;
            }
        }

        // -------------------- PC 端页面解析 --------------------
        private async Task<KsRoomInfo> TryGetFromPcPage(string userId)
        {
            try
            {
                var url = string.Format(KS_PC_LIVE_URL, userId);
                var html = await GetHtmlAsync(url, PC_UA);
                if (string.IsNullOrWhiteSpace(html)) return null;

                var info = new KsRoomInfo { RoomId = userId };

                // 匹配 window.__INITIAL_STATE__={...} 的内容
                // 使用平衡括号匹配来正确处理嵌套的大括号
                var stateMatch = Regex.Match(html, @"window\.__INITIAL_STATE__\s*=\s*(\{)", RegexOptions.Singleline);
                if (!stateMatch.Success)
                {
                    Logger.LogWarn("[KS] PC 页面未找到 __INITIAL_STATE__");
                    return null;
                }

                // 手动解析 JSON，找到匹配的结束括号
                var startIdx = stateMatch.Index + stateMatch.Length - 1; // 第一个 {
                var jsonStr = html.Substring(startIdx);
                var endIdx = FindMatchingBrace(jsonStr, 0);
                if (endIdx < 0)
                {
                    Logger.LogWarn("[KS] PC 页面 __INITIAL_STATE__ JSON 解析失败");
                    return null;
                }
                var jsonContent = jsonStr.Substring(0, endIdx + 1);
                JObject state;
                try
                {
                    state = JObject.Parse(jsonContent);
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("[KS] PC 页面 __INITIAL_STATE__ JSON 解析失败: " + ex.Message);
                    return null;
                }

                // 尝试多种路径获取直播数据
                var liveData = state.SelectToken("$..liveDetailList[0]") 
                              ?? state.SelectToken("$..liveStream")
                              ?? state.SelectToken("$..liveDetail")
                              ?? state.SelectToken("$.liveStream")
                              ?? state.SelectToken("$.liveDetail");
                
                if (liveData == null)
                {
                    Logger.LogWarn("[KS] PC 页面 state 中未找到直播数据");
                    return null;
                }

                info.LiveStreamId = liveData["liveStreamId"]?.Value<string>() ?? "";
                info.AuthorId = liveData["authorId"]?.Value<string>() ?? "";
                info.AuthorName = liveData["author"]?["name"]?.Value<string>() ?? "";
                info.Title = liveData["caption"]?.Value<string>() ?? "";

                Logger.LogInfo($"[KS] PC端页面解析成功: liveStreamId={info.LiveStreamId}, authorId={info.AuthorId}, authorName={info.AuthorName}");

                var wsInfo = await GetDanmuWsInfo(info.LiveStreamId, info.AuthorId, html);
                if (wsInfo != null)
                {
                    info.Token = wsInfo.Item1;
                    info.WebSocketUrls = wsInfo.Item2;
                }

                info.IsLive = !string.IsNullOrWhiteSpace(info.LiveStreamId);
                return info;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] PC 端页面解析失败: " + ex.Message);
                return null;
            }
        }

        // -------------------- 获取弹幕 WS 连接信息 --------------------
        private async Task<Tuple<string, List<string>>> GetDanmuWsInfo(string liveStreamId, string authorId, string rawHtml = null)
        {
            if (string.IsNullOrWhiteSpace(liveStreamId)) return null;
            try
            {
                var token = TryExtractTokenFromHtml(rawHtml);
                var wsUrls = BuildDefaultWsUrls(liveStreamId, token);

                var apiResult = await TryGetWsInfoFromApi(liveStreamId, authorId);
                if (apiResult != null)
                {
                    if (!string.IsNullOrWhiteSpace(apiResult.Item1)) token = apiResult.Item1;
                    if (apiResult.Item2 != null && apiResult.Item2.Any()) wsUrls = apiResult.Item2;
                }

                return Tuple.Create(token, wsUrls);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 获取弹幕 WS 信息失败: " + ex.Message);
                return null;
            }
        }

        private string TryExtractTokenFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var m = Regex.Match(html, @"""token""\s*:\s*""([^""]{10,})""");
            return m.Success ? m.Groups[1].Value : "";
        }

        private List<string> BuildDefaultWsUrls(string liveStreamId, string token)
        {
            var wsUrls = new List<string>();
            var hosts = new[] {
                "live-ws-group.kuaishou.com",
                "live-ws.kuaishou.com",
                "live-ws-pg-group1.kuaishou.com",
                "live-ws-pg-group2.kuaishou.com",
                "live-ws-pg-group3.kuaishou.com"
            };
            foreach (var host in hosts)
            {
                wsUrls.Add($"wss://{host}{KS_DANMU_WS_PATH}");
                wsUrls.Add($"wss://{host}{KS_DANMU_WS_PATH_LEGACY}" +
                           $"?liveStreamId={Uri.EscapeDataString(liveStreamId)}" +
                           $"&token={Uri.EscapeDataString(token ?? "")}" +
                           $"&did=web_{Guid.NewGuid():N}");
            }
            return wsUrls;
        }

        private async Task<Tuple<string, List<string>>> TryGetWsInfoFromApi(string liveStreamId, string authorId)
        {
            try
            {
                var graphqlResult = await TryGetWsInfoFromGraphQL(liveStreamId, authorId);
                if (graphqlResult != null) return graphqlResult;
                return await TryGetWsInfoFromLegacyApi(liveStreamId, authorId);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 获取 WS 信息失败: " + ex.Message);
                return null;
            }
        }

        private async Task<Tuple<string, List<string>>> TryGetWsInfoFromGraphQL(string liveStreamId, string authorId)
        {
            try
            {
                var apiUrl = "https://live.kuaishou.com/live_graphql";
                var bodyObj = new
                {
                    operationName = "liveDetail",
                    variables = new { principalId = liveStreamId, page = "detail" },
                    query = @"query liveDetail($principalId: String!, $page: String!) {
                        liveDetail(principalId: $principalId, page: $page) {
                            chatRoomInfo { token webSocketUrls }
                        }
                    }"
                };
                var bodyJson = JsonConvert.SerializeObject(bodyObj);

                var json = await PostJsonAsync(apiUrl, bodyJson, PC_UA, $"https://live.kuaishou.com/u/{authorId}");
                if (json == null) return null;

                var jobj = JObject.Parse(json);
                var chatRoomInfo = jobj.SelectToken("$.data.liveDetail.chatRoomInfo");
                if (chatRoomInfo == null)
                {
                    Logger.LogWarn("[KS] GraphQL API 未返回 chatRoomInfo");
                    return null;
                }

                var token = chatRoomInfo["token"]?.Value<string>() ?? "";
                var wsUrls = chatRoomInfo["webSocketUrls"]?.Select(u => u.Value<string>()).ToList() ?? new List<string>();
                Logger.LogInfo($"[KS] GraphQL API 获取成功, token长度={token.Length}, wsUrl数量={wsUrls.Count}");
                return Tuple.Create(token, wsUrls);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] GraphQL API 调用失败: " + ex.Message);
                return null;
            }
        }

        private async Task<Tuple<string, List<string>>> TryGetWsInfoFromLegacyApi(string liveStreamId, string authorId)
        {
            try
            {
                var apiUrl = "https://live.kuaishou.com/api/kuaishou/live/web/im/init";
                var bodyObj = new { liveStreamId = liveStreamId, pageId = GeneratePageId() };
                var bodyJson = JsonConvert.SerializeObject(bodyObj);

                var json = await PostJsonAsync(apiUrl, bodyJson, PC_UA, $"https://live.kuaishou.com/u/{authorId}");
                if (json == null) return null;

                var jobj = JObject.Parse(json);
                var result = jobj["result"]?.Value<int>() ?? 0;
                if (result != 1)
                {
                    Logger.LogWarn($"[KS] IM Init API 返回非成功: result={result}");
                    return null;
                }

                var chatRoomInfo = jobj["data"]?["chatRoomInfo"];
                var token = chatRoomInfo?["token"]?.Value<string>() ?? "";
                var wsUrls = chatRoomInfo?["webSocketUrls"]?.Select(u => u.Value<string>()).ToList() ?? new List<string>();
                Logger.LogInfo($"[KS] 旧版 IM Init API 获取成功, token长度={token.Length}, wsUrl数量={wsUrls.Count}");
                return Tuple.Create(token, wsUrls);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 旧版 IM Init API 调用失败: " + ex.Message);
                return null;
            }
        }

        // -------------------- HTTP 辅助（HttpWebRequest，无需 NuGet HttpClient）--------------------

        private async Task<string> GetHtmlAsync(string url, string userAgent)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.UserAgent = userAgent;
                    req.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                    req.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
                    req.Timeout = TIMEOUT_MS;
                    req.AllowAutoRedirect = true;
                    if (!string.IsNullOrWhiteSpace(_cookie))
                        req.Headers.Add("Cookie", _cookie);

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarn($"[KS] GET {url} 失败: {ex.Message}");
                    return null;
                }
            });
        }

        private async Task<string> PostJsonAsync(string url, string jsonBody, string userAgent, string referer)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "POST";
                    req.UserAgent = userAgent;
                    req.ContentType = "application/json";
                    req.ContentLength = bodyBytes.Length;
                    req.Timeout = TIMEOUT_MS;
                    req.AllowAutoRedirect = true;
                    req.Headers.Add("Origin", "https://live.kuaishou.com");
                    req.Referer = referer;
                    if (!string.IsNullOrWhiteSpace(_cookie))
                        req.Headers.Add("Cookie", _cookie);

                    using (var stream = req.GetRequestStream())
                    {
                        stream.Write(bodyBytes, 0, bodyBytes.Length);
                    }

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarn($"[KS] POST {url} 失败: {ex.Message}");
                    return null;
                }
            });
        }

        private static string GeneratePageId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        /// <summary>
        /// 找到字符串中从指定位置开始的匹配右括号
        /// </summary>
        private static int FindMatchingBrace(string s, int start)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                
                if (inString) continue;
                
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            
            return -1; // 未找到匹配的右括号
        }

        public void Dispose() { }
    }
}
