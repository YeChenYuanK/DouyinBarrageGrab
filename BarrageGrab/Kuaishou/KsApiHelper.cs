using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// 参考 https://github.com/wbt5/real-url
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
        private readonly HttpClient _httpClient;
        private static string _cookie = ""; // 可配置 Cookie 以提升稳定性

        public KsApiHelper()
        {
            var handler = new HttpClientHandler()
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                ServerCertificateCustomValidationCallback = (msg, cert, chain, err) => true
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }

        /// <summary>
        /// 设置全局 Cookie（从浏览器复制，提升稳定性）
        /// </summary>
        public static void SetCookie(string cookie) => _cookie = cookie ?? "";

        /// <summary>
        /// 通过快手主播的短ID/用户ID 获取直播间信息及 WebSocket 连接参数
        /// </summary>
        /// <param name="userId">快手主播的直播间短链 ID（例如 https://live.kuaishou.com/u/xxxxx 中的 xxxxx）</param>
        public async Task<KsRoomInfo> GetRoomInfoAsync(string userId)
        {
            // 优先尝试移动端页面（更稳定）
            var info = await TryGetFromMobilePage(userId);
            if (info != null && info.IsLive) return info;

            // 移动端失败时 fallback 到 PC 端
            info = await TryGetFromPcPage(userId);
            return info;
        }

        // -------------------- 移动端页面解析 --------------------
        private async Task<KsRoomInfo> TryGetFromMobilePage(string userId)
        {
            try
            {
                var url = string.Format(KS_MOBILE_LIVE_URL, userId);
                var html = await GetHtml(url, MOBILE_UA);
                if (string.IsNullOrWhiteSpace(html)) return null;

                var info = new KsRoomInfo { RoomId = userId };

                // 尝试多种正则匹配 liveStream JSON 块
                JObject liveStream = null;
                
                // 方法1：标准匹配 liveStream":{...},"obfuseData
                var liveStreamMatch = Regex.Match(html, @"""liveStream"":(\{.*?\})"",""?obfuseData", RegexOptions.Singleline);
                if (liveStreamMatch.Success)
                {
                    var liveStreamJson = liveStreamMatch.Groups[1].Value;
                    try { liveStream = JObject.Parse(liveStreamJson); }
                    catch { }
                }
                
                // 方法2：匹配 multiResolutionHlsPlayUrls 结构（real-url 项目方式）
                if (liveStream == null)
                {
                    var hlsMatch = Regex.Match(html, @"""multiResolutionHlsPlayUrls"":\s*(\[.*?\])", RegexOptions.Singleline);
                    if (hlsMatch.Success)
                    {
                        Logger.LogInfo("[KS] 使用 HLS 播放地址匹配方式");
                    }
                }
                
                // 方法3：从 window.__INITIAL_STATE__ 或类似变量中提取
                if (liveStream == null)
                {
                    var stateMatch = Regex.Match(html, @"window\.__INITIAL_STATE__\s*=\s*({.+?})\s*;?\s*</script>", RegexOptions.Singleline);
                    if (stateMatch.Success)
                    {
                        try
                        {
                            var state = JObject.Parse(stateMatch.Groups[1].Value);
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
                    if (html.Contains("liveStreamId"))
                        Logger.LogInfo("[KS] 页面包含 liveStreamId 关键字");
                    // 打印页面前 2000 字符帮助调试
                    Logger.LogInfo("[KS] 移动端页面内容(前2000字符): " + (html.Length > 2000 ? html.Substring(0, 2000) : html));
                    return null;
                }

                info.LiveStreamId = liveStream["liveStreamId"]?.Value<string>() ?? "";
                info.AuthorId = liveStream["authorId"]?.Value<string>() ?? "";
                info.AuthorName = liveStream["author"]?["name"]?.Value<string>() ?? "";
                info.Title = liveStream["caption"]?.Value<string>() ?? "";
                info.CoverUrl = liveStream["coverUrls"]?.FirstOrDefault()?.Value<string>() ?? "";

                Logger.LogInfo($"[KS] 移动端页面解析成功: liveStreamId={info.LiveStreamId}, authorId={info.AuthorId}, authorName={info.AuthorName}, isLive={!string.IsNullOrWhiteSpace(info.LiveStreamId)}");

                // 获取 WebSocket 弹幕连接信息
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
                var html = await GetHtml(url, PC_UA);
                if (string.IsNullOrWhiteSpace(html)) return null;

                var info = new KsRoomInfo { RoomId = userId };

                // 快手 PC 页面中的直播信息嵌在 __INITIAL_STATE__ 或类似变量里
                var stateMatch = Regex.Match(html,
                    @"window\.__INITIAL_STATE__\s*=\s*(\{.*?\})(?:\s*;?\s*</script>|window\.)",
                    RegexOptions.Singleline);

                if (!stateMatch.Success)
                {
                    Logger.LogWarn("[KS] PC 页面未找到 __INITIAL_STATE__");
                    return null;
                }

                var state = JObject.Parse(stateMatch.Groups[1].Value);

                // 尝试从 state 里提取直播数据
                var liveData = state.SelectToken("..liveStream") ?? state.SelectToken("..liveDetail");
                if (liveData == null)
                {
                    Logger.LogWarn("[KS] PC 页面 state 中未找到 liveStream/liveDetail");
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
                    Logger.LogInfo($"[KS] WS信息(PC): token长度={info.Token?.Length ?? 0}, wsUrls数量={info.WebSocketUrls?.Count ?? 0}");
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

        // -------------------- 获取弹幕 WebSocket 连接信息 --------------------
        /// <summary>
        /// 查询弹幕 WebSocket 连接 token 和服务器地址
        /// </summary>
        private async Task<Tuple<string, List<string>>> GetDanmuWsInfo(string liveStreamId, string authorId, string rawHtml = null)
        {
            if (string.IsNullOrWhiteSpace(liveStreamId)) return null;
            try
            {
                // 快手弹幕直连参数可以从页面 HTML 中的初始 token 提取
                // 也可以通过 im/init API 动态获取
                var token = TryExtractTokenFromHtml(rawHtml);

                // 构造默认 WS 地址（即使 API 获取失败也能直连）
                var wsUrls = BuildDefaultWsUrls(liveStreamId, token);

                // 尝试通过 API 动态刷新 token 和 WS 地址
                var apiResult = await TryGetWsInfoFromApi(liveStreamId, authorId);
                if (apiResult != null)
                {
                    if (!string.IsNullOrWhiteSpace(apiResult.Item1))
                        token = apiResult.Item1;
                    if (apiResult.Item2 != null && apiResult.Item2.Any())
                        wsUrls = apiResult.Item2;
                }

                return Tuple.Create(token, wsUrls);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 获取弹幕 WS 信息失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 尝试从原始 HTML 中提取初始 token
        /// </summary>
        private string TryExtractTokenFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            // 常见特征：\"token\":\"xxxxx\"
            var m = Regex.Match(html, @"""token""\s*:\s*""([^""]{10,})""");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// 构造默认 WS URL（快手标准弹幕接口）
        /// 新版本使用 /websocket 路径，参数通过 Protobuf 认证帧发送
        /// </summary>
        private List<string> BuildDefaultWsUrls(string liveStreamId, string token)
        {
            // 快手弹幕 WebSocket 地址格式（新版本）
            // 新版使用 /websocket 路径，认证参数通过 Protobuf 帧发送
            var wsUrls = new List<string>();
            
            // 尝试多个可能的域名
            var hosts = new[] {
                "live-ws-group.kuaishou.com",
                "live-ws.kuaishou.com",
                "live-ws-pg-group1.kuaishou.com",
                "live-ws-pg-group2.kuaishou.com",
                "live-ws-pg-group3.kuaishou.com"
            };
            
            foreach (var host in hosts)
            {
                // 新版 WebSocket 地址（/websocket 路径，参数在认证帧中）
                var url1 = $"wss://{host}{KS_DANMU_WS_PATH}";
                wsUrls.Add(url1);
                
                // 备用：带查询参数的旧版格式
                var url2 = $"wss://{host}{KS_DANMU_WS_PATH_LEGACY}" +
                           $"?liveStreamId={Uri.EscapeDataString(liveStreamId)}" +
                           $"&token={Uri.EscapeDataString(token ?? "")}" +
                           $"&did=web_{Guid.NewGuid():N}";
                wsUrls.Add(url2);
            }
            
            return wsUrls;
        }

        /// <summary>
        /// 通过快手 IM Init API 动态获取 token 和 WS 地址
        /// </summary>
        private async Task<Tuple<string, List<string>>> TryGetWsInfoFromApi(string liveStreamId, string authorId)
        {
            try
            {
                // 尝试新版 GraphQL API
                var graphqlResult = await TryGetWsInfoFromGraphQL(liveStreamId, authorId);
                if (graphqlResult != null) return graphqlResult;
                
                // Fallback 到旧版 API
                var apiResult = await TryGetWsInfoFromLegacyApi(liveStreamId, authorId);
                return apiResult;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 获取 WS 信息失败: " + ex.Message);
                return null;
            }
        }
        
        /// <summary>
        /// 通过 GraphQL API 获取弹幕连接信息（新版方式）
        /// </summary>
        private async Task<Tuple<string, List<string>>> TryGetWsInfoFromGraphQL(string liveStreamId, string authorId)
        {
            try
            {
                var apiUrl = "https://live.kuaishou.com/live_graphql";
                var body = new
                {
                    operationName = "liveDetail",
                    variables = new
                    {
                        principalId = liveStreamId,
                        page = "detail"
                    },
                    query = @"query liveDetail($principalId: String!, $page: String!) {
                        liveDetail(principalId: $principalId, page: $page) {
                            liveStream {
                                id
                                liveStreamId
                                caption
                                playUrls {
                                    quality
                                    url
                                }
                            }
                            streamArgs {
                                streamId
                                token
                            }
                            chatRoomInfo {
                                token
                                webSocketUrls
                            }
                        }
                    }"
                };

                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("User-Agent", PC_UA);
                request.Headers.Add("Origin", "https://live.kuaishou.com");
                request.Headers.Add("Referer", $"https://live.kuaishou.com/u/{authorId}");
                if (!string.IsNullOrWhiteSpace(_cookie))
                    request.Headers.Add("Cookie", _cookie);

                var response = await _httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                var jobj = JObject.Parse(json);

                var chatRoomInfo = jobj.SelectToken("$.data.liveDetail.chatRoomInfo");
                if (chatRoomInfo == null)
                {
                    Logger.LogWarn("[KS] GraphQL API 未返回 chatRoomInfo");
                    return null;
                }

                var token = chatRoomInfo["token"]?.Value<string>() ?? "";
                var wsUrls = chatRoomInfo["webSocketUrls"]?.Select(u => u.Value<string>()).ToList()
                             ?? new List<string>();
                
                Logger.LogInfo($"[KS] GraphQL API 获取成功, token长度={token.Length}, wsUrl数量={wsUrls.Count}");
                return Tuple.Create(token, wsUrls);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] GraphQL API 调用失败: " + ex.Message);
                return null;
            }
        }
        
        /// <summary>
        /// 通过旧版 API 获取弹幕连接信息
        /// </summary>
        private async Task<Tuple<string, List<string>>> TryGetWsInfoFromLegacyApi(string liveStreamId, string authorId)
        {
            try
            {
                // 快手网页端弹幕连接初始化接口
                var apiUrl = "https://live.kuaishou.com/api/kuaishou/live/web/im/init";
                var body = new
                {
                    liveStreamId = liveStreamId,
                    pageId = GeneratePageId()
                };

                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("User-Agent", PC_UA);
                request.Headers.Add("Origin", "https://live.kuaishou.com");
                request.Headers.Add("Referer", $"https://live.kuaishou.com/u/{authorId}");
                if (!string.IsNullOrWhiteSpace(_cookie))
                    request.Headers.Add("Cookie", _cookie);

                var response = await _httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                var jobj = JObject.Parse(json);

                var result = jobj["result"]?.Value<int>() ?? 0;
                if (result != 1)
                {
                    Logger.LogWarn($"[KS] IM Init API 返回非成功: result={result}");
                    return null;
                }

                var chatRoomInfo = jobj["data"]?["chatRoomInfo"];
                var token = chatRoomInfo?["token"]?.Value<string>() ?? "";
                var wsUrls = chatRoomInfo?["webSocketUrls"]?.Select(u => u.Value<string>()).ToList()
                             ?? new List<string>();

                Logger.LogInfo($"[KS] 旧版 IM Init API 获取成功, token长度={token.Length}, wsUrl数量={wsUrls.Count}");
                return Tuple.Create(token, wsUrls);
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS] 旧版 IM Init API 调用失败: " + ex.Message);
                return null;
            }
        }

        // -------------------- 辅助方法 --------------------
        private async Task<string> GetHtml(string url, string userAgent)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", userAgent);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
            if (!string.IsNullOrWhiteSpace(_cookie))
                request.Headers.Add("Cookie", _cookie);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarn($"[KS] HTTP {(int)response.StatusCode} for {url}");
                return null;
            }
            return await response.Content.ReadAsStringAsync();
        }

        private static string GeneratePageId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}
