using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using BarrageGrab.Kuaishou;
using BarrageGrab.Modles;
using BarrageGrab.Modles.ProtoEntity;
using JsonEntity = BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Proxy;
using BarrageGrab.Proxy.ProxyEventArgs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;

namespace BarrageGrab
{
    /// <summary>
    /// 本机Wss弹幕抓取器
    /// </summary>
    public class WssBarrageGrab : IDisposable
    {
        //ISystemProxy proxy = new FiddlerProxy();
        ISystemProxy proxy = new TitaniumProxy();
        AppSetting appsetting = AppSetting.Current;
        private readonly object ksFlowLock = new object();
        private readonly Dictionary<string, KsFlowClusterStat> ksFlowClusters = new Dictionary<string, KsFlowClusterStat>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ksRouteStateLastLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ksHttpFocusLastLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ksDecodedUrlLastLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ksPushHitLastLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KsHttpHostStat> ksHttpHostStats = new Dictionary<string, KsHttpHostStat>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KsDomainClusterStat> ksDomainClusterStats = new Dictionary<string, KsDomainClusterStat>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ksDomainClusterLastLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime ksHttpHostLastEmitAt = DateTime.MinValue;

        /// <summary>
        /// 进入直播间
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<MemberMessage>> OnMemberMessage;

        /// <summary>
        /// 关注
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<SocialMessage>> OnSocialMessage;

        /// <summary>
        /// 聊天
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<ChatMessage>> OnChatMessage;

        /// <summary>
        /// 点赞
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<LikeMessage>> OnLikeMessage;

        /// <summary>
        /// 礼物
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<GiftMessage>> OnGiftMessage;

        /// <summary>
        /// 直播间统计
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<RoomUserSeqMessage>> OnRoomUserSeqMessage;

        /// <summary>
        /// 直播间状态变更
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<ControlMessage>> OnControlMessage;

        /// <summary>
        /// 粉丝团消息
        /// </summary>
        public event EventHandler<RoomMessageEventArgs<FansclubMessage>> OnFansclubMessage;

        /// <summary>
        /// 快手代理弹幕事件（通过直播伴侣代理抓取的快手弹幕）
        /// </summary>
        public event EventHandler<KsBarragePusher.BarrageEventArgs> OnKuaishouProxyBarrage;

        /// <summary>
        /// 代理
        /// </summary>
        public ISystemProxy Proxy { get { return proxy; } }

        public WssBarrageGrab()
        {
            proxy.OnWebSocketData += Proxy_OnWebSocketData;
            proxy.OnFetchResponse += Proxy_OnFetchResponse;
        }

        public void Start()
        {
            proxy.Start();
        }

        public void Dispose()
        {
            proxy.Dispose();
        }


        //gzip解压缩
        private byte[] Decompress(byte[] zippedData)
        {
            MemoryStream ms = new MemoryStream(zippedData);
            GZipStream compressedzipStream = new GZipStream(ms, CompressionMode.Decompress);
            MemoryStream outBuffer = new MemoryStream();
            byte[] block = new byte[1024];
            while (true)
            {
                int bytesRead = compressedzipStream.Read(block, 0, block.Length);
                if (bytesRead <= 0)
                    break;
                else
                    outBuffer.Write(block, 0, bytesRead);
            }
            compressedzipStream.Close();
            return outBuffer.ToArray();
        }

        //ws数据处理
        private void Proxy_OnWebSocketData(object sender, WsMessageEventArgs e)
        {
            Logger.LogInfo($"[WS入口] ★★★ Proxy_OnWebSocketData 被调用 Host={e.HostName} Process={e.ProcessName} Len={e.Payload?.Length ?? -1} NeedDecomp={e.NeedDecompress}");
            var buff = e.Payload;
            if (buff != null && buff.Length > 0)
            {
                DumpKuaishouRawBytes("ws_any_raw", $"{e.HostName}|{e.ProcessName}", buff);
                if (IsUnknownFlowHost(e.HostName))
                {
                    DumpKuaishouRawBytes("ws_unknown_host_raw", $"{e.HostName}|{e.ProcessName}", buff);
                }
            }
            var processName = (e.ProcessName ?? string.Empty).Trim();
            var hostName = (e.HostName ?? string.Empty).Trim().ToLowerInvariant();
            var allowByProcessFilter = appsetting.ProcessFilter != null && appsetting.ProcessFilter.Any(f =>
            {
                var filter = (f ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(filter)) return false;
                return processName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            });
            var isLikelyKuaishouProcess =
                processName.IndexOf("kwailive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.IndexOf("kscloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.IndexOf("kuaishou", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.IndexOf("gifshow", StringComparison.OrdinalIgnoreCase) >= 0;
            var isKuaishouHost = hostName.Contains("kuaishou") || hostName.Contains("wsukwai") || hostName.Contains("gifshow") || hostName.Contains("ksapis") || hostName.StartsWith("ksraw:") || hostName.StartsWith("ksrawtx:");
            // 快手官方客户端抓包场景允许按域名放行，但仍要求进程特征，避免浏览器/其他应用噪音误触发
            var allowKuaishouBypass = isKuaishouHost && (isLikelyKuaishouProcess || hostName.StartsWith("ksraw:") || hostName.StartsWith("ksrawtx:"));
            if (!allowByProcessFilter && !allowKuaishouBypass)
            {
                Logger.LogInfo($"[WS] 进程被过滤: {e.ProcessName}，不在白名单内");
                return;
            }
            if (buff == null || buff.Length == 0)
            {
                Logger.LogInfo($"[WS] 空数据包，跳过");
                return;
            }

            Logger.LogInfo($"[WS] 收到数据 Host={e.HostName} Process={e.ProcessName} Len={buff.Length} NeedDecompress={e.NeedDecompress}");

            // 判断是否为快手弹幕请求
            if (isKuaishouHost)
            {
                RecordKuaishouFlow("ws", e.HostName, string.Empty, e.ProcessName, buff.Length, string.Empty, TryDecodeFlowHintText(buff));
                Logger.LogInfo($"[WS] 识别为快手域名，转交ProcessKuaishouWsData");
                ProcessKuaishouWsData(e);
                return;
            }

            //如果需要Gzip解压缩，但是开头字节不符合Gzip特征字节 则不处理
            if (e.NeedDecompress && buff[0] != 0x08)
            {
                Logger.LogInfo($"[WS] Gzip数据但字节头不对({buff[0]:X2})，跳过");
                return;
            }

            try
            {
                var enty = Serializer.Deserialize<WssResponse>(new ReadOnlyMemory<byte>(buff));
                if (enty == null)
                {
                    Logger.LogInfo($"[WS] WssResponse解析为null，跳过");
                    return;
                }

                //检测包格式
                if (!enty.Headers.Any(a => a.Key == "compress_type" && a.Value == "gzip"))
                {
                    Logger.LogInfo($"[WS] 无compress_type=gzip头，跳过");
                    return;
                }

                byte[] allBuff;
                //解压gzip
                allBuff = e.NeedDecompress ? Decompress(enty.Payload) : enty.Payload;
                var response = Serializer.Deserialize<Response>(new ReadOnlyMemory<byte>(allBuff));

                Logger.LogInfo($"[WS] 抖音解析成功，消息数={response.Messages.Count}");
                response.Messages.ForEach(f => DoMessage(f, e.ProcessName));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"处理弹幕数据包时出错:{ex.Message}");
            }
        }

        // 处理快手 WebSocket 数据
        private void ProcessKuaishouWsData(WsMessageEventArgs e)
        {
            var buff = e.Payload;
            if (buff.Length == 0) return;

            DumpKuaishouRawBytes("ws_raw", e.HostName, buff);
            Logger.LogInfo($"[快手] ProcessKuaishouWsData 收到数据 Len={buff.Length}");
            if (buff.Length >= 120 || (e.HostName ?? "").StartsWith("ksraw:", StringComparison.OrdinalIgnoreCase) || (e.HostName ?? "").StartsWith("ksrawtx:", StringComparison.OrdinalIgnoreCase))
            {
                LogKuaishouPacketSignature(buff, e.HostName);
            }

            // 专门针对上行原始帧（send）做认证参数抽取，优先解决 token 抓不到的问题。
            if ((e.HostName ?? string.Empty).StartsWith("ksrawtx:", StringComparison.OrdinalIgnoreCase))
            {
                TryCaptureKsRuntimeParamFromRawWsPacket(buff, e.HostName, e.ProcessName, "ws.raw.tx");
            }

            bool protobufParsed = false;
            string protobufErr = null;
            try
            {
                // 尝试 Protobuf 解析（支持偏移探测、去头探测，兼容自定义二进制头）
                protobufParsed = TryProcessKuaishouProtobufWithOffsets(buff, e.ProcessName);
                if (protobufParsed) return;
            }
            catch (Exception ex)
            {
                protobufErr = ex.Message;
            }

            // 快手网页端常见资料包：评论正文常在外层帧切片后的 payload 中
            if (TryProcessKuaishouProfileChatPacketWithCandidates(buff))
            {
                return;
            }

            // 基于实战样本：大量评论事件包在 raw/candidate 层可抽出文本 token，但未命中标准 envelope。
            if (TryProcessKuaishouBusinessPacketWithCandidates(buff, e.ProcessName))
            {
                return;
            }

            // 二层协议：外层二进制头 + 内层 GZIP（日志已确认存在 1F 8B 08）
            if (TryProcessKuaishouEmbeddedGzip(buff, e.ProcessName)) return;

            // 最后兜底 JSON
            try
            {
                ProcessKuaishouJson(buff, e.ProcessName);
            }
            catch (Exception ex2)
            {
                Logger.LogWarn($"[快手] WebSocket 数据解析失败: Protobuf({protobufErr ?? "no-hit"}), JSON({ex2.Message})");
            }
        }

        private void TryCaptureKsRuntimeParamFromRawWsPacket(byte[] buff, string hostName, string processName, string sourceTag)
        {
            try
            {
                if (buff == null || buff.Length == 0) return;

                // 先尝试结构化 proto 命中
                if (TryCaptureKsRuntimeParamFromProtoBytes(buff, sourceTag, processName))
                {
                    return;
                }

                // send 帧常见为 token 长串，先提取 token，再使用 synthetic id 兜底存储
                var token = TryPickKsBinaryTokenCandidate(buff);
                if (string.IsNullOrWhiteSpace(token)) return;

                var syntheticLiveStreamId = BuildSyntheticKsLiveStreamId(processName);
                var rawHost = (hostName ?? string.Empty).Trim();
                if (rawHost.StartsWith("ksrawtx:", StringComparison.OrdinalIgnoreCase))
                {
                    rawHost = rawHost.Substring("ksrawtx:".Length);
                }
                else if (rawHost.StartsWith("ksraw:", StringComparison.OrdinalIgnoreCase))
                {
                    rawHost = rawHost.Substring("ksraw:".Length);
                }

                var wsUrl = string.IsNullOrWhiteSpace(rawHost) ? string.Empty : ("wss://" + rawHost);
                AppRuntime.KsRuntimeParams?.Upsert(syntheticLiveStreamId, token, wsUrl, sourceTag + ".candidate");
                Logger.LogInfo($"[KS_RUNTIME_CAPTURE_WS_SEND] source={sourceTag}.candidate process={processName} liveStreamId={syntheticLiveStreamId} tokenLen={(token?.Length ?? 0)} host={rawHost}");
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS_RUNTIME_CAPTURE_WS_SEND_FAIL] " + ex.Message);
            }
        }

        private bool TryProcessKuaishouProfileChatPacketWithCandidates(byte[] buff)
        {
            if (buff == null || buff.Length < 60) return false;

            // 先尝试原始包
            if (TryProcessKuaishouProfileChatPacketCore(buff, "raw"))
            {
                return true;
            }

            // 再尝试外层帧切片候选（针对 Win 侧 433B/444B 这类包）
            foreach (var candidate in BuildKuaishouDecodeCandidates(buff))
            {
                var len = candidate.Data.Length - candidate.Offset;
                if (len < 60) continue;

                var payload = new byte[len];
                Buffer.BlockCopy(candidate.Data, candidate.Offset, payload, 0, len);
                if (TryProcessKuaishouProfileChatPacketCore(payload, candidate.Strategy))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly List<string> _ksBizPacketDedup = new List<string>();
        private readonly object _ksBizPacketDedupLock = new object();

        private bool TryPushKuaishouBizPacketDedup(string key)
        {
            lock (_ksBizPacketDedupLock)
            {
                if (_ksBizPacketDedup.Contains(key)) return false;
                _ksBizPacketDedup.Add(key);
                while (_ksBizPacketDedup.Count > 240) _ksBizPacketDedup.RemoveAt(0);
                return true;
            }
        }

        private bool TryProcessKuaishouBusinessPacketWithCandidates(byte[] buff, string processName)
        {
            if (buff == null || buff.Length < 64) return false;

            var probes = new List<(byte[] payload, string strategy)>();
            probes.Add((buff, "raw"));
            foreach (var c in BuildKuaishouDecodeCandidates(buff).Take(24))
            {
                var len = c.Data.Length - c.Offset;
                if (len < 64) continue;
                var payload = new byte[len];
                Buffer.BlockCopy(c.Data, c.Offset, payload, 0, len);
                probes.Add((payload, c.Strategy));
            }

            foreach (var probe in probes)
            {
                var tokens = ExtractProtoStringTokens(probe.payload, maxTokens: 128);
                if (tokens.Count == 0) continue;
                var expanded = ExpandKuaishouTokensWithBase64(tokens, maxTokens: 192);
                var score = ScoreKuaishouBusinessPacket(expanded, probe.payload.Length);
                if (score <= 0) continue;

                var chatCandidate = expanded
                    .Where(t => IsLikelyKuaishouChatText(t))
                    .Where(t => !IsLikelyKuaishouNickname(t))
                    .Where(t => !ContainsGuidLikeFragment(t))
                    .OrderByDescending(t => t.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF))
                    .ThenBy(t => t.Length)
                    .FirstOrDefault();

                var sigTop = string.Join("|", expanded.Take(4)).Replace("\r", "").Replace("\n", " ");
                var dedupKey = $"{probe.strategy}:{probe.payload.Length}:{sigTop}";
                if (!TryPushKuaishouBizPacketDedup(dedupKey)) continue;

                Logger.LogInfo($"[KS_BIZ_PACKET] strategy={probe.strategy} len={probe.payload.Length} score={score} process={processName} top={sigTop}");

                if (string.IsNullOrWhiteSpace(chatCandidate)) continue;
                if (score < 38) continue;
                if (!TryPushKuaishouFallbackText(chatCandidate)) continue;

                var nickname = ResolveKuaishouNickname(expanded, chatCandidate);
                Logger.LogInfo($"[快手][Fallback][BIZ_PACKET] 命中评论 strategy={probe.strategy}, score={score}, nickname={nickname}, content={chatCandidate}, len={probe.payload.Length}");
                FireKuaishouChat(new Modles.ProtoEntity.KsChatMessage
                {
                    Content = chatCandidate,
                    User = new Modles.ProtoEntity.KsUser
                    {
                        Nickname = nickname,
                        UserId = "",
                        HeadUrl = ""
                    }
                });
                return true;
            }

            return false;
        }

        private List<string> ExpandKuaishouTokensWithBase64(List<string> tokens, int maxTokens)
        {
            var output = new List<string>();
            foreach (var raw in tokens ?? new List<string>())
            {
                var t = (raw ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                output.Add(t);
                if (output.Count >= maxTokens) break;
                if (TryDecodeBase64Utf8(t, out var decoded) && !string.IsNullOrWhiteSpace(decoded))
                {
                    output.Add(decoded.Trim());
                    if (output.Count >= maxTokens) break;
                }
            }
            return output
                .Select(s => (s ?? string.Empty).Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .Take(maxTokens)
                .ToList();
        }

        private int ScoreKuaishouBusinessPacket(List<string> tokens, int payloadLen)
        {
            if (tokens == null || tokens.Count == 0) return 0;
            var score = 0;
            if (payloadLen >= 600 && payloadLen <= 7000) score += 8;
            if (tokens.Any(t => t.IndexOf("uhead", StringComparison.OrdinalIgnoreCase) >= 0)) score += 6;
            if (tokens.Any(t => t.IndexOf("yximgs.com", StringComparison.OrdinalIgnoreCase) >= 0)) score += 6;
            if (tokens.Any(t => t.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0)) score += 18;
            if (tokens.Any(t => t.IndexOf("fans_group_comment_bg", StringComparison.OrdinalIgnoreCase) >= 0)) score += 16;

            var chatLike = tokens.Where(IsLikelyKuaishouChatText).ToList();
            if (chatLike.Count > 0) score += 26;
            if (chatLike.Any(t => t.IndexOf("主播", StringComparison.OrdinalIgnoreCase) >= 0)) score += 8;
            if (chatLike.Any(t => t.IndexOf("你好", StringComparison.OrdinalIgnoreCase) >= 0)) score += 6;

            // 礼物事件通常和评论混在一起，轻微降权防止误把纯礼物提示当评论。
            if (tokens.Any(IsLikelyGiftTriggerToken)) score -= 6;
            return score;
        }

        private bool TryProcessKuaishouProfileChatPacketCore(byte[] payload, string strategy)
        {
            if (payload == null || payload.Length < 60) return false;

            var tokens = ExtractProtoStringTokens(payload, maxTokens: 96);
            if (tokens.Count == 0) return false;

            // 该簇在样本中稳定包含资料字段：level/rank/audience
            var hasProfileMeta = tokens.Any(t =>
                t.IndexOf("level_label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("rank_top", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("audienceRank", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasProfileMeta) return false;

            // 多用户名批量包（名册/榜单）噪音高，先跳过避免误报
            var cjkLikeCount = tokens.Count(t => t.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF) >= 2);
            if (cjkLikeCount > 8) return false;

            if (TryProcessKuaishouProfileGiftPacket(tokens, strategy, payload.Length))
            {
                return true;
            }

            var candidates = new List<(string text, bool fromBase64)>();
            foreach (var token in tokens)
            {
                var t = (token ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                candidates.Add((t, false));

                // 部分评论正文在该簇中以 base64 文本存储，例如 "DOS4u+aSreS9oOWlvQ==" => "主播你好"
                if (TryDecodeBase64Utf8(t, out var decoded) && !string.IsNullOrWhiteSpace(decoded))
                {
                    candidates.Add((decoded.Trim(), true));
                }
            }

            var best = candidates
                .Select(c => new { c.text, c.fromBase64, score = ScoreKuaishouProfileChatCandidate(c.text, c.fromBase64) })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.text.Length)
                .FirstOrDefault();
            if (best == null) return false;
            if (ContainsGuidLikeFragment(best.text)) return false;
            if (!TryPushKuaishouFallbackText(best.text)) return false;

            var nickname = ResolveKuaishouNickname(tokens, best.text);

            Logger.LogInfo($"[快手][Fallback][PROFILE] 命中评论 strategy={strategy}, nickname={nickname}, content={best.text}, fromBase64={best.fromBase64}, len={payload.Length}");
            FireKuaishouChat(new Modles.ProtoEntity.KsChatMessage
            {
                Content = best.text,
                User = new Modles.ProtoEntity.KsUser
                {
                    Nickname = nickname,
                    UserId = "",
                    HeadUrl = ""
                }
            });
            return true;
        }

        private bool TryProcessKuaishouProfileGiftPacket(List<string> tokens, string strategy, int payloadLen)
        {
            if (tokens == null || tokens.Count == 0) return false;

            var expanded = new List<string>();
            foreach (var raw in tokens)
            {
                var t = (raw ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                expanded.Add(t);
                if (TryDecodeBase64Utf8(t, out var decoded) && !string.IsNullOrWhiteSpace(decoded))
                {
                    expanded.Add(decoded.Trim());
                }
            }

            bool hasGiftVerb = expanded.Any(IsLikelyGiftTriggerToken);
            if (!hasGiftVerb) return false;

            long giftCount = 1;
            var countToken = expanded.FirstOrDefault(IsLikelyGiftCountToken);
            if (!string.IsNullOrWhiteSpace(countToken))
            {
                giftCount = ParseGiftCount(countToken);
                if (giftCount <= 0) giftCount = 1;
            }

            var nickname = ResolveKuaishouNickname(expanded, "");
            if (string.IsNullOrWhiteSpace(nickname)) nickname = "快手用户";

            string giftName = expanded.FirstOrDefault(t =>
                t.IndexOf("礼物", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("赠送", StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.IsNullOrWhiteSpace(giftName)) giftName = "礼物";

            var dedupKey = $"{nickname}|{giftName}|{giftCount}|{strategy}";
            if (!TryPushKuaishouFallbackGift(dedupKey)) return false;

            Logger.LogInfo($"[快手][Fallback][PROFILE_GIFT] strategy={strategy}, nickname={nickname}, gift={giftName}, count={giftCount}, len={payloadLen}");
            FireKuaishouGift(new Modles.ProtoEntity.KsGiftMessage
            {
                User = new Modles.ProtoEntity.KsUser
                {
                    Nickname = nickname,
                    UserId = "",
                    HeadUrl = ""
                },
                GiftName = giftName,
                Count = giftCount
            });
            return true;
        }

        private bool TryPushKuaishouFallbackGift(string dedupKey)
        {
            lock (_ksFallbackGiftDedupLock)
            {
                if (_ksFallbackGiftDedup.Contains(dedupKey)) return false;
                _ksFallbackGiftDedup.Add(dedupKey);
                while (_ksFallbackGiftDedup.Count > 120) _ksFallbackGiftDedup.RemoveAt(0);
                return true;
            }
        }

        private bool TryProcessKuaishouEmbeddedGzip(byte[] buff, string processName)
        {
            var hit = 0;
            for (int i = 0; i <= buff.Length - 3; i++)
            {
                if (buff[i] != 0x1F || buff[i + 1] != 0x8B || buff[i + 2] != 0x08) continue;
                hit++;
                try
                {
                    var gzip = new byte[buff.Length - i];
                    Buffer.BlockCopy(buff, i, gzip, 0, gzip.Length);
                    DumpKuaishouRawBytes($"ws_gzip_raw_at_{i}", processName, gzip);
                    var inflated = Decompress(gzip);
                    if (inflated == null || inflated.Length == 0) continue;
                    DumpKuaishouRawBytes($"ws_gzip_inflated_at_{i}", processName, inflated);

                    Logger.LogInfo($"[快手] GZIP命中 at={i} inflatedLen={inflated.Length}");
                    DumpKsReverseSamples(buff, inflated, processName, i);
                    var wireSummary = BuildProtoWireSummary(inflated, maxFields: 24);
                    var isBroadcastCandidate = IsLikelyKuaishouBroadcastWire(wireSummary);
                    if (isBroadcastCandidate)
                    {
                        Logger.LogInfo($"[KS_REVERSE] classify=broadcast wire={wireSummary}");
                    }
                    LogKuaishouStateTextProbe(inflated, wireSummary);
                    TryCaptureKsRuntimeParamFromInflated(inflated, $"ws.gzip.at.{i}", processName);
                    LogKuaishouPacketSignature(inflated, $"ksgzip:{i}");
                    // hints 更适合做观测，不再直接触发评论，避免“江北/王翠花”类误报
                    TryLogKuaishouSessionInfo(inflated, allowFallbackEmit: false);
                    if (TryProcessKuaishouJsonFragments(inflated, allowChatEmit: !isBroadcastCandidate))
                    {
                        Logger.LogInfo($"[快手] GZIP解包后 JSON片段解析命中 at={i}");
                        return true;
                    }

                    if (TryProcessKuaishouProtobufWithOffsets(inflated, processName))
                    {
                        Logger.LogInfo($"[快手] GZIP解包后 Protobuf 解析成功 at={i}");
                        return true;
                    }

                    try
                    {
                        ProcessKuaishouJson(inflated, processName);
                        Logger.LogInfo($"[快手] GZIP解包后 JSON 解析成功 at={i}");
                        return true;
                    }
                    catch
                    {
                        // ignore, continue scan
                    }

                    // 最终兜底：做无 schema 的 protobuf 文本提取，先把评论文本打通
                    if (!isBroadcastCandidate && TryProcessKuaishouGenericProtoText(inflated))
                    {
                        Logger.LogInfo($"[快手] GZIP解包后 通用文本提取命中 at={i}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogInfo($"[快手] GZIP解包失败 at={i}: {ex.Message}");
                }

                // 一个包通常只会有 1-2 段 gzip，避免过度扫描
                if (hit >= 3) break;
            }

            return false;
        }

        private void TryCaptureKsRuntimeParamFromInflated(byte[] inflated, string sourceTag, string processName)
        {
            try
            {
                if (inflated == null || inflated.Length == 0) return;

                // 先尝试 protobuf 结构化提取（优先命中 token）。
                if (TryCaptureKsRuntimeParamFromProtoBytes(inflated, sourceTag, processName))
                {
                    return;
                }

                var text = Encoding.UTF8.GetString(inflated);

                Func<string[], string> firstMatch = patterns =>
                {
                    foreach (var p in patterns)
                    {
                        var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
                        if (m.Success && m.Groups.Count > 1 && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                        {
                            return m.Groups[1].Value;
                        }
                    }
                    return "";
                };

                var liveStreamId = firstMatch(new[]
                {
                    @"liveStreamId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})",
                    @"live_stream_id[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})",
                    @"streamId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})"
                });
                if (string.IsNullOrWhiteSpace(liveStreamId))
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        liveStreamId = firstMatch(new[]
                        {
                            @"roomId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})",
                            @"room_id[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})"
                        });
                    }
                }

                var token = firstMatch(new[]
                {
                    @"websocketToken[=:\""\\s]{0,6}([A-Za-z0-9_\-\.%+/=]{8,512})",
                    @"wsToken[=:\""\\s]{0,6}([A-Za-z0-9_\-\.%+/=]{8,512})",
                    @"accessToken[=:\""\\s]{0,6}([A-Za-z0-9_\-\.%+/=]{8,512})",
                    @"serviceToken[=:\""\\s]{0,6}([A-Za-z0-9_\-\.%+/=]{8,512})",
                    @"token[=:\""\\s]{0,6}([A-Za-z0-9_\-\.%+/=]{8,512})"
                });

                // 文本路径没有 token 时，走一次二进制候选提取（离线样本稳定出现 len=216 候选）。
                string tokenSource = sourceTag;
                if (string.IsNullOrWhiteSpace(token))
                {
                    var tokenCandidate = TryPickKsBinaryTokenCandidate(inflated);
                    if (!string.IsNullOrWhiteSpace(tokenCandidate))
                    {
                        token = tokenCandidate;
                        tokenSource = sourceTag + ".bin.candidate";
                    }
                }

                // 如果 token 已有但 liveStreamId 暂时缺失，用稳定的进程维度 key 存储，供 GetLatest 回退路径使用。
                if (string.IsNullOrWhiteSpace(liveStreamId) && !string.IsNullOrWhiteSpace(token))
                {
                    liveStreamId = BuildSyntheticKsLiveStreamId(processName);
                }
                if (string.IsNullOrWhiteSpace(liveStreamId)) return;

                var wsUrl = Regex.Match(text ?? string.Empty, @"wss://[^\s\""']+", RegexOptions.IgnoreCase).Value;

                AppRuntime.KsRuntimeParams?.Upsert(liveStreamId, token, wsUrl, tokenSource);
                Logger.LogInfo($"[KS_RUNTIME_CAPTURE_TEXT] source={tokenSource} process={processName} liveStreamId={liveStreamId} tokenLen={(token?.Length ?? 0)}");
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS_RUNTIME_CAPTURE_TEXT_FAIL] " + ex.Message);
            }
        }

        private string BuildSyntheticKsLiveStreamId(string processName)
        {
            var raw = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName.Trim().ToLowerInvariant();
            raw = Regex.Replace(raw, @"[^a-z0-9_\-]", "_");
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = "unknown";
            }
            return "ks_runtime_" + raw;
        }

        private string TryPickKsBinaryTokenCandidate(byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0) return string.Empty;
                var candidates = new HashSet<string>(StringComparer.Ordinal);

                CollectKsTokenCandidatesFromProto(data, depth: 0, maxDepth: 3, candidates: candidates, maxCandidates: 128);
                foreach (var c in BuildKuaishouDecodeCandidates(data).Take(32))
                {
                    try
                    {
                        if (c.Data == null || c.Offset < 0 || c.Offset >= c.Data.Length) continue;
                        var seg = new byte[c.Data.Length - c.Offset];
                        Buffer.BlockCopy(c.Data, c.Offset, seg, 0, seg.Length);
                        CollectKsTokenCandidatesFromProto(seg, depth: 0, maxDepth: 3, candidates: candidates, maxCandidates: 128);
                    }
                    catch
                    {
                        // ignore candidate decode failure
                    }
                }

                string best = string.Empty;
                int bestScore = int.MinValue;
                foreach (var v in candidates)
                {
                    var score = ScoreKsTokenCandidate(v);
                    if (score > bestScore || (score == bestScore && v.Length > (best?.Length ?? 0)))
                    {
                        best = v;
                        bestScore = score;
                    }
                }

                // 阈值避免误判：至少需要“长串 + base64样式”特征。
                if (string.IsNullOrWhiteSpace(best) || bestScore < 8) return string.Empty;
                Logger.LogInfo($"[KS_RUNTIME_BIN_CANDIDATE] tokenLen={best.Length} score={bestScore}");
                return best;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void CollectKsTokenCandidatesFromProto(byte[] data, int depth, int maxDepth, HashSet<string> candidates, int maxCandidates)
        {
            if (data == null || data.Length == 0 || candidates == null) return;
            if (depth > maxDepth || candidates.Count >= maxCandidates) return;

            int i = 0;
            int end = data.Length;
            while (i < end && candidates.Count < maxCandidates)
            {
                if (!TryReadVarint64(data, ref i, out ulong key)) break;
                int wireType = (int)(key & 0x07);

                switch (wireType)
                {
                    case 0:
                        if (!TryReadVarint64(data, ref i, out _)) return;
                        break;
                    case 1:
                        i += 8;
                        if (i > end) return;
                        break;
                    case 2:
                        if (!TryReadVarint64(data, ref i, out ulong lenU)) return;
                        if (lenU > int.MaxValue) return;
                        int len = (int)lenU;
                        if (len < 0 || i + len > end) return;
                        if (len >= 16 && len <= 1024)
                        {
                            var seg = new byte[len];
                            Buffer.BlockCopy(data, i, seg, 0, len);
                            TryExtractTokenCandidatesFromString(Encoding.ASCII.GetString(seg), candidates, maxCandidates);
                            TryExtractTokenCandidatesFromString(Encoding.UTF8.GetString(seg), candidates, maxCandidates);
                            CollectKsTokenCandidatesFromProto(seg, depth + 1, maxDepth, candidates, maxCandidates);
                        }
                        i += len;
                        break;
                    case 5:
                        i += 4;
                        if (i > end) return;
                        break;
                    default:
                        return;
                }
            }
        }

        private void TryExtractTokenCandidatesFromString(string text, HashSet<string> candidates, int maxCandidates)
        {
            if (string.IsNullOrWhiteSpace(text) || candidates == null || candidates.Count >= maxCandidates) return;
            var ms = Regex.Matches(text, @"[A-Za-z0-9_\-\.%+/=]{64,512}");
            foreach (Match m in ms)
            {
                var v = (m.Value ?? string.Empty).Trim();
                if (!IsLikelyKsToken(v)) continue;
                candidates.Add(v);
                if (candidates.Count >= maxCandidates) return;
            }
        }

        private int ScoreKsTokenCandidate(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return int.MinValue;
            int s = 0;
            if (token.Length >= 200) s += 5;
            else if (token.Length >= 160) s += 4;
            else if (token.Length >= 96) s += 3;
            else if (token.Length >= 64) s += 2;
            if (token.Contains("=")) s += 2;
            if (token.Contains("/") || token.Contains("+")) s += 2;
            if (token.Contains("_") || token.Contains("-") || token.Contains(".")) s += 1;
            return s;
        }

        private bool IsLikelyKsLiveStreamId(string liveStreamId)
        {
            if (string.IsNullOrWhiteSpace(liveStreamId)) return false;
            return Regex.IsMatch(liveStreamId.Trim(), @"^[A-Za-z0-9_\-]{8,128}$");
        }

        private bool IsLikelyKsToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            token = token.Trim();
            if (token.Length < 64 || token.Length > 1024) return false;
            return Regex.IsMatch(token, @"^[A-Za-z0-9_\-\.%+/=]+$");
        }

        private bool IsValidKsAuthRequest(Modles.ProtoEntity.KsAuthRequest auth, out string reason)
        {
            reason = string.Empty;
            if (auth == null)
            {
                reason = "auth=null";
                return false;
            }

            var liveStreamId = (auth.LiveStreamId ?? string.Empty).Trim();
            var token = (auth.Token ?? string.Empty).Trim();
            if (!IsLikelyKsLiveStreamId(liveStreamId))
            {
                reason = "bad_liveStreamId";
                return false;
            }
            if (!IsLikelyKsToken(token))
            {
                reason = "bad_token";
                return false;
            }

            var kpn = (auth.Kpn ?? string.Empty).Trim();
            var kpf = (auth.Kpf ?? string.Empty).Trim();
            if ((!string.IsNullOrWhiteSpace(kpn) && !kpn.Equals("LIVE_STREAM", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(kpf) && !kpf.Equals("WEB", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "bad_kpn_kpf";
                return false;
            }
            return true;
        }

        private bool TryCaptureKsRuntimeParamFromProtoBytes(byte[] data, string sourceTag, string processName)
        {
            try
            {
                // A) 直接把当前 bytes 当作 KsAuthRequest 试解
                try
                {
                    var directAuth = Serializer.Deserialize<Modles.ProtoEntity.KsAuthRequest>(new ReadOnlyMemory<byte>(data));
                    if (IsValidKsAuthRequest(directAuth, out var rejectReason))
                    {
                        AppRuntime.KsRuntimeParams?.Upsert(directAuth.LiveStreamId, directAuth.Token, "", sourceTag + ".proto.directAuth");
                        Logger.LogInfo($"[KS_RUNTIME_CAPTURE_PROTO] source={sourceTag}.proto.directAuth process={processName} liveStreamId={directAuth.LiveStreamId} tokenLen={(directAuth.Token?.Length ?? 0)}");
                        return true;
                    }
                    if (directAuth != null)
                    {
                        Logger.LogInfo($"[KS_RUNTIME_CAPTURE_PROTO_REJECT] source={sourceTag}.proto.directAuth reason={rejectReason} liveStreamId={directAuth.LiveStreamId} tokenLen={(directAuth.Token?.Length ?? 0)}");
                    }
                }
                catch
                {
                    // ignore direct-auth failures
                }

                // B) 当作 KsSocketMessage 信封试解，再按 payloadType 判断
                foreach (var c in BuildKuaishouDecodeCandidates(data).Take(32))
                {
                    try
                    {
                        var span = new ReadOnlyMemory<byte>(c.Data, c.Offset, c.Data.Length - c.Offset);
                        var envelope = Serializer.Deserialize<Modles.ProtoEntity.KsSocketMessage>(span);
                        if (envelope?.Payload == null || envelope.Payload.Length == 0) continue;
                        var payloadType = (envelope.PayloadType ?? string.Empty).Trim();

                        // 200 = auth request
                        if (payloadType == "200" || payloadType.Equals("AUTH", StringComparison.OrdinalIgnoreCase))
                        {
                            var auth = Serializer.Deserialize<Modles.ProtoEntity.KsAuthRequest>(new ReadOnlyMemory<byte>(envelope.Payload));
                            if (IsValidKsAuthRequest(auth, out var rejectReason))
                            {
                                AppRuntime.KsRuntimeParams?.Upsert(auth.LiveStreamId, auth.Token, "", sourceTag + ".proto.envelopeAuth");
                                Logger.LogInfo($"[KS_RUNTIME_CAPTURE_PROTO] source={sourceTag}.proto.envelopeAuth process={processName} liveStreamId={auth.LiveStreamId} tokenLen={(auth.Token?.Length ?? 0)}");
                                return true;
                            }
                            if (auth != null)
                            {
                                Logger.LogInfo($"[KS_RUNTIME_CAPTURE_PROTO_REJECT] source={sourceTag}.proto.envelopeAuth reason={rejectReason} liveStreamId={auth.LiveStreamId} tokenLen={(auth.Token?.Length ?? 0)} payloadType={payloadType}");
                            }
                        }
                    }
                    catch
                    {
                        // keep trying
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn("[KS_RUNTIME_CAPTURE_PROTO_FAIL] " + ex.Message);
            }

            return false;
        }

        private void LogKuaishouStateTextProbe(byte[] inflated, string wireSummary)
        {
            try
            {
                if (inflated == null || inflated.Length == 0) return;
                var hash = ComputeSha1Hex(inflated).Substring(0, 12);

                var text = Encoding.UTF8.GetString(inflated);
                if (string.IsNullOrWhiteSpace(text)) return;

                var keywordRegex = new Regex(@"开播|下播|标题|直播间|liveStatus|startTime|liveTitle|welcome|online", RegexOptions.IgnoreCase);
                var keywordHits = keywordRegex.Matches(text).Cast<Match>().Select(m => m.Value).Distinct().Take(32).ToList();
                var zhChunks = Regex.Matches(text, @"[\u4e00-\u9fa5A-Za-z0-9_\-:]{2,64}")
                    .Cast<Match>()
                    .Select(m => m.Value)
                    .Where(s => s.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct()
                    .Take(30)
                    .ToList();
                var preview = new string(text.Take(600).Select(c => char.IsControl(c) ? '.' : c).ToArray());
                Logger.LogInfo($"[KS_STATE_TEXT_PROBE] hash={hash} keys={string.Join("|", keywordHits)} wire={wireSummary}");
                Logger.LogInfo($"[KS_STATE_TEXT_PROBE] chunks={string.Join("|", zhChunks)}");
                Logger.LogInfo($"[KS_STATE_TEXT_PROBE] preview={preview}");
            }
            catch
            {
                // ignore
            }
        }

        private bool IsLikelyKuaishouBroadcastWire(string wireSummary)
        {
            if (string.IsNullOrWhiteSpace(wireSummary)) return false;
            // 来自样本分群：活动/公告广播簇有稳定字段形态
            bool clusterA = wireSummary.Contains("f1:len(11),f2:v(2),f3:v(")
                         && wireSummary.Contains("f6:len(392)")
                         && wireSummary.Contains("f12:len(9)");
            bool clusterB = wireSummary.Contains("f1:len(41),f2:v(2),f3:v(3)")
                         && wireSummary.Contains("f10:len(172)")
                         && wireSummary.Contains("f11:v(7000)")
                         && wireSummary.Contains("f18:len(106)");
            bool clusterC = wireSummary.Contains("f9:len(533)")
                         && wireSummary.Contains("f40:len(10)")
                         && wireSummary.Contains("f47:len(1)");
            return clusterA || clusterB || clusterC;
        }

        private readonly HashSet<string> _ksReverseSampleDedup = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _ksReverseSampleDedupLock = new object();
        private void DumpKsReverseSamples(byte[] raw, byte[] inflated, string processName, int gzipOffset)
        {
            try
            {
                var hash = ComputeSha1Hex(inflated).Substring(0, 16);
                lock (_ksReverseSampleDedupLock)
                {
                    if (_ksReverseSampleDedup.Contains(hash)) return;
                    _ksReverseSampleDedup.Add(hash);
                    while (_ksReverseSampleDedup.Count > 300)
                    {
                        // 简单控量：超过上限后清空去重集合，允许后续新样本继续入库
                        _ksReverseSampleDedup.Clear();
                        break;
                    }
                }

                var ts = DateTime.Now.ToString("HHmmss");
                var proc = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName;
                var nameBase = $"{ts}_{proc}_g{gzipOffset}_{hash}";
                Logger.LogBarragePack("KS-REVERSE-RAW", $"{nameBase}_raw", raw, maxCount: 120);
                Logger.LogBarragePack("KS-REVERSE-INFLATED", $"{nameBase}_inflated", inflated, maxCount: 180);
                Logger.LogInfo($"[KS_REVERSE] sampleDump hash={hash} rawLen={raw.Length} inflatedLen={inflated.Length}");

                var wire = BuildProtoWireSummary(inflated, maxFields: 24);
                if (!string.IsNullOrWhiteSpace(wire))
                {
                    Logger.LogInfo($"[KS_REVERSE] wireSummary={wire}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_REVERSE] dumpFailed {ex.Message}");
            }
        }

        private string ComputeSha1Hex(byte[] data)
        {
            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(data ?? Array.Empty<byte>());
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private string BuildProtoWireSummary(byte[] data, int maxFields)
        {
            if (data == null || data.Length == 0) return "";
            var parts = new List<string>();
            int idx = 0;
            int count = 0;
            while (idx < data.Length && count < maxFields)
            {
                var keyPos = idx;
                if (!TryReadVarint64(data, ref idx, out ulong key)) break;
                int fieldNo = (int)(key >> 3);
                int wireType = (int)(key & 0x07);
                switch (wireType)
                {
                    case 0:
                        if (!TryReadVarint64(data, ref idx, out ulong v0)) return string.Join(",", parts);
                        parts.Add($"f{fieldNo}:v({v0})");
                        break;
                    case 1:
                        if (idx + 8 > data.Length) return string.Join(",", parts);
                        parts.Add($"f{fieldNo}:64");
                        idx += 8;
                        break;
                    case 2:
                        if (!TryReadVarint64(data, ref idx, out ulong lenU)) return string.Join(",", parts);
                        if (lenU > int.MaxValue) return string.Join(",", parts);
                        int len = (int)lenU;
                        if (idx + len > data.Length) return string.Join(",", parts);
                        parts.Add($"f{fieldNo}:len({len})");
                        idx += len;
                        break;
                    case 5:
                        if (idx + 4 > data.Length) return string.Join(",", parts);
                        parts.Add($"f{fieldNo}:32");
                        idx += 4;
                        break;
                    default:
                        parts.Add($"f{fieldNo}:wt({wireType})@{keyPos}");
                        return string.Join(",", parts);
                }
                count++;
            }
            return string.Join(",", parts);
        }

        private bool TryProcessKuaishouJsonFragments(byte[] inflated, bool allowChatEmit = true)
        {
            try
            {
                var text = Encoding.UTF8.GetString(inflated);
                if (string.IsNullOrWhiteSpace(text)) return false;

                var candidates = ExtractJsonObjectCandidates(text, 16);
                if (candidates.Count == 0) return false;

                var foundStateCallback = false;
                foreach (var json in candidates)
                {
                    JObject jobj;
                    try { jobj = JObject.Parse(json); }
                    catch { continue; }

                    var jsonFlat = jobj.ToString(Formatting.None);
                    var isActivityNoise = IsKuaishouActivityNoisePayload(jsonFlat);

                    var title = jobj.SelectToken("$..title")?.Value<string>();
                    var anchor = jobj.SelectToken("$..nickname")?.Value<string>()
                                 ?? jobj.SelectToken("$..authorName")?.Value<string>()
                                 ?? jobj.SelectToken("$..anchorName")?.Value<string>();
                    var liveStatus = jobj.SelectToken("$..liveStatus")?.ToString()
                                  ?? jobj.SelectToken("$..status")?.ToString();
                    var start = jobj.SelectToken("$..start")?.ToString()
                             ?? jobj.SelectToken("$..startTime")?.ToString();
                    var online = jobj.SelectToken("$..online")?.ToString()
                              ?? jobj.SelectToken("$..onlineCount")?.ToString();
                    var probeKeys = new[] { "title", "start", "liveStatus", "room", "anchor", "welcome", "online", "liveTitle", "开播", "下播", "结束" };
                    var hitKeys = probeKeys.Where(k => jsonFlat.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    var probePreview = jsonFlat.Length > 220 ? jsonFlat.Substring(0, 220) + "..." : jsonFlat;
                    Logger.LogInfo($"[KS_STATE_PROBE] noise={isActivityNoise} title={title ?? "N/A"} anchor={anchor ?? "N/A"} liveStatus={liveStatus ?? "N/A"} start={start ?? "N/A"} online={online ?? "N/A"} keys={string.Join("|", hitKeys)} preview={probePreview}");
                    if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(anchor))
                    {
                        // 原始候选日志：用于人工核对 title/nickname 是否真实出现，不参与状态判定
                        Logger.LogInfo($"[KS_ROOM_JSON_RAW] noise={isActivityNoise} anchor={anchor ?? "N/A"}, title={title ?? "N/A"}");
                        Logger.PrintColor($"[快手房间原始候选] noise={isActivityNoise} | 主播: {anchor ?? "N/A"} | 标题: {title ?? "N/A"}", ConsoleColor.DarkGray);
                    }
                    if (!isActivityNoise && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(anchor)))
                    {
                        Logger.LogInfo($"[KS_ROOM_JSON] anchor={anchor ?? "N/A"}, title={title ?? "N/A"}");
                        // 恢复窗口提示：用于开播阶段快速观察，注意这里是 JSON 片段候选信息
                        Logger.PrintColor($"[快手房间候选] 主播: {anchor ?? "N/A"} | 标题: {title ?? "N/A"}", ConsoleColor.Cyan);
                    }

                    var hasExplicitStateField = jsonFlat.IndexOf("\"liveStatus\":", StringComparison.OrdinalIgnoreCase) >= 0
                        || (!string.IsNullOrWhiteSpace(liveStatus) && !string.Equals(liveStatus, "N/A", StringComparison.OrdinalIgnoreCase));
                    if (!isActivityNoise && hasExplicitStateField)
                    {
                        foundStateCallback = true;
                        var stateTitle = title ?? jobj.SelectToken("$..liveTitle")?.Value<string>() ?? "N/A";
                        var stateAnchor = anchor ?? jobj.SelectToken("$..userName")?.Value<string>() ?? "N/A";
                        Logger.LogInfo($"[KS_STATE_CONFIRMED] anchor={stateAnchor}, title={stateTitle}, liveStatus={liveStatus ?? "N/A"}, start={start ?? "N/A"}, online={online ?? "N/A"}");
                        Logger.PrintColor($"[快手状态确认] 主播: {stateAnchor} | 标题: {stateTitle} | liveStatus={liveStatus ?? "N/A"}", ConsoleColor.Green);
                    }
                    else if (!isActivityNoise && IsKuaishouStateCallbackJobj(jobj))
                    {
                        foundStateCallback = true;
                        var stateTitle = title ?? jobj.SelectToken("$..liveTitle")?.Value<string>() ?? "N/A";
                        var stateAnchor = anchor ?? jobj.SelectToken("$..userName")?.Value<string>() ?? "N/A";
                        Logger.LogInfo($"[KS_STATE_CANDIDATE] anchor={stateAnchor}, title={stateTitle}, liveStatus={liveStatus ?? "N/A"}, start={start ?? "N/A"}, online={online ?? "N/A"}, keys={string.Join("|", hitKeys)}");
                        Logger.PrintColor($"[快手状态候选] 主播: {stateAnchor} | 标题: {stateTitle}", ConsoleColor.DarkCyan);
                    }

                    foreach (var obj in jobj.DescendantsAndSelf().OfType<JObject>())
                    {
                        var content = obj["content"]?.Value<string>()
                                   ?? obj["comment"]?.Value<string>()
                                   ?? obj["text"]?.Value<string>()
                                   ?? obj["message"]?.Value<string>()
                                   ?? obj["msg"]?.Value<string>();
                        if (!allowChatEmit) continue;
                        if (!IsLikelyKuaishouChatText(content)) continue;
                        if (!TryPushKuaishouFallbackText(content)) continue;

                        var nickname = obj["nickname"]?.Value<string>()
                                    ?? obj["userName"]?.Value<string>()
                                    ?? obj["name"]?.Value<string>()
                                    ?? "快手用户";
                        if (string.IsNullOrWhiteSpace(nickname)) nickname = "快手用户";

                        Logger.LogInfo($"[快手][Fallback][JSON] 命中评论 nickname={nickname}, content={content}");
                        FireKuaishouChat(new Modles.ProtoEntity.KsChatMessage
                        {
                            Content = content,
                            User = new Modles.ProtoEntity.KsUser
                            {
                                Nickname = nickname,
                                UserId = "",
                                HeadUrl = ""
                            }
                        });
                        return true;
                    }
                }

                // 识别为开播状态回调包时，视为“已处理”，避免后续误判为评论
                return foundStateCallback;
            }
            catch
            {
                return false;
            }
        }

        private bool IsKuaishouStateCallbackJobj(JObject jobj)
        {
            if (jobj == null) return false;
            var allText = jobj.ToString(Formatting.None);
            if (string.IsNullOrWhiteSpace(allText)) return false;
            if (IsKuaishouActivityNoisePayload(allText)) return false;
            var keys = new[] { "title", "start", "liveStatus", "room", "anchor", "welcome", "online", "liveTitle" };
            var score = keys.Count(k => allText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            return score >= 3;
        }

        private bool IsKuaishouActivityNoisePayload(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("livePeakCup", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("MERCHANT_", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("stickerImagePendant", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("mentionModuleGuide", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("巅峰赛红包", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<string> ExtractJsonObjectCandidates(string text, int maxCount)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '{') continue;
                var depth = 0;
                var inString = false;
                var escaped = false;
                for (int j = i; j < text.Length; j++)
                {
                    var c = text[j];
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') { inString = !inString; continue; }
                    if (inString) continue;
                    if (c == '{') depth++;
                    if (c == '}') depth--;
                    if (depth != 0) continue;

                    var len = j - i + 1;
                    if (len >= 24 && len <= 20000)
                    {
                        list.Add(text.Substring(i, len));
                        if (list.Count >= maxCount) return list;
                    }
                    i = j;
                    break;
                }
            }
            return list;
        }

        private readonly List<string> _ksSessionDedup = new List<string>();
        private readonly object _ksSessionDedupLock = new object();
        private readonly List<string> _ksRoomDedup = new List<string>();
        private readonly object _ksRoomDedupLock = new object();
        private void TryLogKuaishouSessionInfo(byte[] inflated, bool allowFallbackEmit = true)
        {
            try
            {
                var text = Encoding.UTF8.GetString(inflated);
                var guid = Regex.Match(text, @"[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}");
                var zh = Regex.Matches(text, @"[\u4e00-\u9fa5]{2,20}")
                    .Cast<Match>()
                    .Select(m => m.Value)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .Take(5)
                    .ToList();

                if (!guid.Success && zh.Count == 0) return;
                var key = $"{(guid.Success ? guid.Value : "noguid")}::{string.Join("|", zh)}";
                lock (_ksSessionDedupLock)
                {
                    if (_ksSessionDedup.Contains(key)) return;
                    _ksSessionDedup.Add(key);
                    while (_ksSessionDedup.Count > 80) _ksSessionDedup.RemoveAt(0);
                }

                Logger.LogInfo($"[KS_SESSION] sessionId={(guid.Success ? guid.Value : "N/A")} hints={string.Join(",", zh)}");
                TryLogKuaishouRoleHint(zh);
                TryLogKuaishouRoomInfo(guid.Success ? guid.Value : "N/A", zh);
                if (allowFallbackEmit)
                {
                    TryEmitFallbackChatFromHints(zh);
                }
            }
            catch
            {
                // ignore
            }
        }

        private void TryLogKuaishouRoomInfo(string sessionId, List<string> hints)
        {
            if (hints == null || hints.Count == 0) return;
            var tokens = hints.Select(h => (h ?? string.Empty).Trim()).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            if (!tokens.Any()) return;

            // 常见结构：主播名,评论词,观众名,人在看
            var anchor = tokens.FirstOrDefault() ?? "";
            var roomTitle = tokens.FirstOrDefault(t => t.Contains("直播间") || t.Contains("红包") || t.Contains("抽手机")) ?? "";
            var onlineHint = tokens.FirstOrDefault(t => t.Contains("人在看")) ?? "";

            // 若未命中 roomTitle，用第二段作为标题候选（避免空日志）
            if (string.IsNullOrWhiteSpace(roomTitle) && tokens.Count >= 2)
            {
                roomTitle = tokens[1];
            }

            var key = $"{sessionId}|{anchor}|{roomTitle}|{onlineHint}";
            lock (_ksRoomDedupLock)
            {
                if (_ksRoomDedup.Contains(key)) return;
                _ksRoomDedup.Add(key);
                while (_ksRoomDedup.Count > 120) _ksRoomDedup.RemoveAt(0);
            }

            // 注意：hints 是候选语义片段，不保证是正式房间字段，避免误导只打印为候选信息
            Logger.LogInfo($"[KS_ROOM_HINT] sessionId={sessionId}, anchorCandidate={anchor}, titleCandidate={roomTitle}, onlineHint={onlineHint}");
        }

        private void TryLogKuaishouRoleHint(List<string> hints)
        {
            if (hints == null || hints.Count < 2) return;
            var tokens = hints.Select(h => (h ?? string.Empty).Trim()).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            if (!tokens.Any()) return;

            // 常见结构：主播名,评论词,观众名,人在看
            var withoutStatus = tokens.Where(t => t != "人在看").ToList();
            if (withoutStatus.Count >= 3)
            {
                var anchor = withoutStatus[0];
                var comment = withoutStatus[1];
                var audience = withoutStatus[2];
                Logger.LogInfo($"[KS_ROLE] anchor={anchor}, audience={audience}, commentCandidate={comment}");
                return;
            }

            // 兜底：记录可疑昵称与可疑评论片段
            var commentLike = withoutStatus.FirstOrDefault(IsLikelyKuaishouChatText) ?? "";
            Logger.LogInfo($"[KS_ROLE] tokens={string.Join("|", tokens)}, commentLike={commentLike}");
        }

        private readonly List<string> _ksHintEmitDedup = new List<string>();
        private readonly object _ksHintEmitDedupLock = new object();
        private void TryEmitFallbackChatFromHints(List<string> hints)
        {
            if (hints == null || hints.Count == 0) return;
            foreach (var raw in hints)
            {
                var hint = (raw ?? string.Empty).Trim();
                if (!IsLikelyKuaishouChatText(hint)) continue;
                if (!TryPushHintEmitDedup(hint)) continue;

                Logger.LogInfo($"[快手][Fallback] 从会话hints触发评论: {hint}");
                var msg = new Modles.ProtoEntity.KsChatMessage
                {
                    Content = hint,
                    User = new Modles.ProtoEntity.KsUser
                    {
                        Nickname = "快手用户",
                        UserId = "",
                        HeadUrl = ""
                    }
                };
                FireKuaishouChat(msg);
                return;
            }
        }

        private bool TryPushHintEmitDedup(string text)
        {
            lock (_ksHintEmitDedupLock)
            {
                if (_ksHintEmitDedup.Contains(text)) return false;
                _ksHintEmitDedup.Add(text);
                while (_ksHintEmitDedup.Count > 120) _ksHintEmitDedup.RemoveAt(0);
                return true;
            }
        }

        private readonly List<string> _ksFallbackTextDedup = new List<string>();
        private readonly object _ksFallbackTextDedupLock = new object();
        private readonly List<string> _ksFallbackGiftDedup = new List<string>();
        private readonly object _ksFallbackGiftDedupLock = new object();
        private string _ksLastStableNickname = "";

        private bool TryProcessKuaishouGenericProtoText(byte[] data)
        {
            var candidates = new List<string>();
            CollectProtoReadableStrings(data, 0, candidates, 0);
            if (candidates.Count == 0) return false;

            // 优先中文短句，过滤协议字段噪音
            var selected = candidates
                .Select(s => s?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Where(IsLikelyKuaishouChatText)
                .OrderByDescending(s => s.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF))
                .ThenBy(s => s.Length)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(selected)) return false;
            if (!TryPushKuaishouFallbackText(selected)) return false;

            var msg = new Modles.ProtoEntity.KsChatMessage
            {
                Content = selected,
                User = new Modles.ProtoEntity.KsUser
                {
                    Nickname = "快手用户",
                    UserId = "",
                    HeadUrl = ""
                }
            };
            Logger.LogInfo($"[快手][Fallback] 通用文本提取命中: {selected}");
            FireKuaishouChat(msg);
            return true;
        }

        private bool TryPushKuaishouFallbackText(string text)
        {
            lock (_ksFallbackTextDedupLock)
            {
                if (_ksFallbackTextDedup.Contains(text)) return false;
                _ksFallbackTextDedup.Add(text);
                while (_ksFallbackTextDedup.Count > 120) _ksFallbackTextDedup.RemoveAt(0);
                return true;
            }
        }

        private int ScoreKuaishouProfileChatCandidate(string text, bool fromBase64)
        {
            if (!IsLikelyKuaishouChatText(text)) return 0;
            var score = 10;

            if (fromBase64) score += 80;
            if (text.Length == 1 && char.IsDigit(text[0])) score += 120;
            if (text == "0") score -= 300;
            if (text.IndexOf("主播", StringComparison.OrdinalIgnoreCase) >= 0) score += 60;
            if (text.IndexOf("你好", StringComparison.OrdinalIgnoreCase) >= 0) score += 30;
            if (ContainsGuidLikeFragment(text)) score -= 200;
            if (text.IndexOf("人在看", StringComparison.OrdinalIgnoreCase) >= 0) score -= 260;
            if (text.IndexOf("在线", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120;
            if (text.IndexOf("commentSource", StringComparison.OrdinalIgnoreCase) >= 0) score -= 200;
            if (text.IndexOf("BOTTOM_BUTTON", StringComparison.OrdinalIgnoreCase) >= 0) score -= 200;

            // 纯昵称词倾向降分，避免把“王翠花”误当评论
            if (IsLikelyKuaishouNickname(text)) score -= 50;

            var cjkCount = text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            var alphaNumCount = text.Count(char.IsLetterOrDigit);
            if (cjkCount >= 2 && alphaNumCount >= 4) score += 45;

            return score;
        }

        private bool IsLikelyKuaishouNickname(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length < 2 || text.Length > 10) return false;
            if (text.Any(char.IsDigit)) return false;
            if (text.IndexOf("主播", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("你好", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("欢迎", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.Contains("?") || text.Contains("�")) return false;
            if (ContainsGuidLikeFragment(text)) return false;
            var cjkCount = text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            return cjkCount >= 2;
        }

        private string ResolveKuaishouNickname(List<string> tokens, string selectedText)
        {
            var nickname = (tokens ?? new List<string>())
                .Select(s => (s ?? string.Empty).Trim())
                .FirstOrDefault(s => IsLikelyKuaishouNickname(s) && !string.Equals(s, selectedText, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                _ksLastStableNickname = nickname;
                return nickname;
            }

            if (!string.IsNullOrWhiteSpace(_ksLastStableNickname)) return _ksLastStableNickname;
            return "快手用户";
        }

        private bool TryDecodeBase64Utf8(string text, out string decoded)
        {
            decoded = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length < 8 || text.Length > 256) return false;
            if (!Regex.IsMatch(text, @"^[A-Za-z0-9+/=]+$")) return false;
            if (text.Length % 4 != 0) return false;

            try
            {
                var bytes = Convert.FromBase64String(text);
                if (bytes == null || bytes.Length == 0 || bytes.Length > 256) return false;
                var s = Encoding.UTF8.GetString(bytes).Trim();
                if (string.IsNullOrWhiteSpace(s)) return false;
                decoded = s;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<string> ExtractProtoStringTokens(byte[] data, int maxTokens)
        {
            var output = new List<string>();
            CollectProtoStringTokens(data, 0, output, 0, maxTokens);
            return output
                .Select(s => (s ?? string.Empty).Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .Take(maxTokens)
                .ToList();
        }

        private void CollectProtoStringTokens(byte[] data, int offset, List<string> output, int depth, int maxTokens)
        {
            if (data == null || output == null || depth > 4 || output.Count >= maxTokens) return;
            int i = offset;
            int end = data.Length;
            while (i < end && output.Count < maxTokens)
            {
                if (!TryReadVarint64(data, ref i, out ulong key)) break;
                int wireType = (int)(key & 0x07);
                switch (wireType)
                {
                    case 0:
                        if (!TryReadVarint64(data, ref i, out _)) return;
                        break;
                    case 1:
                        i += 8;
                        if (i > end) return;
                        break;
                    case 2:
                        if (!TryReadVarint64(data, ref i, out ulong lenU)) return;
                        if (lenU > int.MaxValue) return;
                        int len = (int)lenU;
                        if (len < 0 || i + len > end) return;

                        if (len >= 1 && len <= 256)
                        {
                            var seg = new byte[len];
                            Buffer.BlockCopy(data, i, seg, 0, len);
                            var text = Encoding.UTF8.GetString(seg);
                            if (IsLikelyTokenText(text))
                            {
                                output.Add(text);
                            }

                            // 继续递归解析嵌套 message
                            CollectProtoStringTokens(seg, 0, output, depth + 1, maxTokens);
                        }
                        i += len;
                        break;
                    case 5:
                        i += 4;
                        if (i > end) return;
                        break;
                    default:
                        return;
                }
            }
        }

        private bool IsLikelyTokenText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length == 0 || text.Length > 256) return false;
            // 过滤不可打印内容
            var printableCount = text.Count(ch => !char.IsControl(ch));
            if (printableCount == 0) return false;
            if ((double)printableCount / text.Length < 0.8) return false;
            return true;
        }

        private bool IsLikelyKuaishouChatText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            // 允许 "1" / "2" / "3" 这类单字符数字评论
            if (text.Length == 1)
            {
                return text[0] == '1' || text[0] == '2' || text[0] == '3' || text[0] == '4';
            }
            if (text.Length > 50) return false;
            if (text.Contains("http") || text.Contains("kwailive://") || text.Contains(".png") || text.Contains(".webp")) return false;
            if (Regex.IsMatch(text, @"^[0-9a-fA-F\-]{16,}$")) return false; // guid/hash
            if (ContainsGuidLikeFragment(text)) return false;
            var blacklist = new[]
            {
                "livePeakCup", "MERCHANT_", "lottie", "stickerImage", "正在看", "直播间正在开启", "host-name", "result",
                "快手平台账号", "未成年人", "严禁主播", "人气里程碑", "欢迎开播",
                "抢红包", "红包", "城市巅峰赛", "人在看", "author_label", "commentSource", "BOTTOM_BUTTON"
            };
            if (blacklist.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return false;

            var cjkCount = text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            var letterOrDigit = text.Count(char.IsLetterOrDigit);
            return cjkCount >= 2 || (letterOrDigit >= 4 && text.Any(ch => ch == ' '));
        }

        private bool ContainsGuidLikeFragment(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            // 典型 GUID 片段: 8-4-4-4-12 或其子串
            return Regex.IsMatch(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}");
        }

        private bool IsLikelyGiftTriggerToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text == "送") return true;
            if (text.IndexOf("送礼", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("赠送", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("礼物", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private bool IsLikelyGiftCountToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            return Regex.IsMatch(text, @"^\d+(\.\d+)?万?$");
        }

        private long ParseGiftCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            text = text.Trim();
            bool wan = text.EndsWith("万", StringComparison.Ordinal);
            if (wan) text = text.Substring(0, text.Length - 1);
            if (!double.TryParse(text, out var n)) return 0;
            if (n <= 0) return 0;
            if (wan) n *= 10000.0;
            return (long)Math.Round(n);
        }

        private void CollectProtoReadableStrings(byte[] data, int offset, List<string> output, int depth)
        {
            if (data == null || output == null || depth > 3) return;
            int i = offset;
            int end = data.Length;

            while (i < end)
            {
                if (!TryReadVarint64(data, ref i, out ulong key)) break;
                int wireType = (int)(key & 0x07);
                switch (wireType)
                {
                    case 0: // varint
                        if (!TryReadVarint64(data, ref i, out _)) return;
                        break;
                    case 1: // 64-bit
                        i += 8;
                        if (i > end) return;
                        break;
                    case 2: // length-delimited
                        if (!TryReadVarint64(data, ref i, out ulong lenU)) return;
                        if (lenU > int.MaxValue) return;
                        int len = (int)lenU;
                        if (len < 0 || i + len > end) return;

                        if (len >= 2)
                        {
                            var seg = new byte[len];
                            Buffer.BlockCopy(data, i, seg, 0, len);
                            try
                            {
                                var str = Encoding.UTF8.GetString(seg);
                                if (str.Any(ch => ch >= 0x20) && str.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF) > 0)
                                {
                                    output.Add(str);
                                }
                            }
                            catch { }

                            // 递归进入嵌套 message
                            CollectProtoReadableStrings(seg, 0, output, depth + 1);
                        }
                        i += len;
                        break;
                    case 5: // 32-bit
                        i += 4;
                        if (i > end) return;
                        break;
                    default:
                        return;
                }
            }
        }

        private bool TryReadVarint64(byte[] data, ref int idx, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (idx < data.Length && shift <= 63)
            {
                byte b = data[idx++];
                value |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private bool TryProcessKuaishouProtobufWithOffsets(byte[] buff, string processName)
        {
            foreach (var candidate in BuildKuaishouDecodeCandidates(buff))
            {
                try
                {
                    var span = new ReadOnlyMemory<byte>(candidate.Data, candidate.Offset, candidate.Data.Length - candidate.Offset);
                    var envelope = Serializer.Deserialize<Modles.ProtoEntity.KsSocketMessage>(span);
                    if (envelope?.Payload == null || envelope.Payload.Length == 0) continue;
                    var ksPayload = Serializer.Deserialize<Modles.ProtoEntity.KsPayload>(new ReadOnlyMemory<byte>(envelope.Payload));
                    if (ksPayload?.SendMessages == null || ksPayload.SendMessages.Count == 0) continue;

                    Logger.LogInfo($"[快手] Protobuf命中 strategy={candidate.Strategy} offset={candidate.Offset}，消息数={ksPayload.SendMessages.Count}");
                    foreach (var sendMsg in ksPayload.SendMessages)
                    {
                        var msgType = (sendMsg.MsgType ?? "").ToUpper();
                        var payload = sendMsg.Payload;
                        if (payload == null) continue;
                        switch (msgType)
                        {
                            case "CHAT":
                                FireKuaishouChat(Serializer.Deserialize<Modles.ProtoEntity.KsChatMessage>(new ReadOnlyMemory<byte>(payload)));
                                break;
                            case "GIFT":
                                FireKuaishouGift(Serializer.Deserialize<Modles.ProtoEntity.KsGiftMessage>(new ReadOnlyMemory<byte>(payload)));
                                break;
                            case "LIKE":
                                FireKuaishouLike(Serializer.Deserialize<Modles.ProtoEntity.KsLikeMessage>(new ReadOnlyMemory<byte>(payload)));
                                break;
                            case "ENTER":
                                FireKuaishouEnter(Serializer.Deserialize<Modles.ProtoEntity.KsEnterMessage>(new ReadOnlyMemory<byte>(payload)));
                                break;
                            case "FOLLOW":
                                FireKuaishouFollow(Serializer.Deserialize<Modles.ProtoEntity.KsFollowMessage>(new ReadOnlyMemory<byte>(payload)));
                                break;
                        }
                    }
                    return true;
                }
                catch
                {
                    // 继续尝试下一个候选
                }
            }
            TryLogReadableSegments(buff);
            return false;
        }

        private sealed class KsDecodeCandidate
        {
            public byte[] Data { get; set; }
            public int Offset { get; set; }
            public string Strategy { get; set; }
        }

        private IEnumerable<KsDecodeCandidate> BuildKuaishouDecodeCandidates(byte[] buff)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void AddCandidate(List<KsDecodeCandidate> list, byte[] data, int offset, string strategy)
            {
                if (data == null || data.Length == 0) return;
                if (offset < 0 || offset >= data.Length) return;
                var key = $"{data.Length}:{offset}:{strategy}";
                if (seen.Add(key))
                {
                    list.Add(new KsDecodeCandidate { Data = data, Offset = offset, Strategy = strategy });
                }
            }

            var candidates = new List<KsDecodeCandidate>();

            // 1) 原始包 + 常见偏移（大包适当扩大扫描窗口）
            var offsetWindow = buff.Length >= 512 ? 160 : 64;
            var maxOffset = Math.Min(offsetWindow, Math.Max(0, buff.Length - 1));
            for (var offset = 0; offset <= maxOffset; offset++)
            {
                AddCandidate(candidates, buff, offset, "raw");
            }

            // 2) 自定义头探测：常见为 [type(1)][len(4)] + payload
            if (buff.Length > 6)
            {
                int beLen = (buff[1] << 24) | (buff[2] << 16) | (buff[3] << 8) | buff[4];
                if (beLen > 0 && beLen <= buff.Length - 5)
                {
                    var payload = new byte[beLen];
                    Buffer.BlockCopy(buff, 5, payload, 0, beLen);
                    AddCandidate(candidates, payload, 0, "type1_beLen4");
                }

                int leLen = buff[1] | (buff[2] << 8) | (buff[3] << 16) | (buff[4] << 24);
                if (leLen > 0 && leLen <= buff.Length - 5)
                {
                    var payload = new byte[leLen];
                    Buffer.BlockCopy(buff, 5, payload, 0, leLen);
                    AddCandidate(candidates, payload, 0, "type1_leLen4");
                }
            }

            // 2.5) 头部 varint 长度探测（len + payload）
            if (TryReadVarint32(buff, 0, out var varLen, out var varBytes))
            {
                if (varLen > 0 && varLen <= buff.Length - varBytes)
                {
                    var payload = new byte[varLen];
                    Buffer.BlockCopy(buff, varBytes, payload, 0, varLen);
                    AddCandidate(candidates, payload, 0, "varint_len");
                }
            }

            // 3) 固定帧头裁剪尝试
            foreach (var headerLen in new[] { 1, 2, 4, 8, 12, 16, 20, 24, 28, 32 })
            {
                if (buff.Length > headerLen + 8)
                {
                    var payload = new byte[buff.Length - headerLen];
                    Buffer.BlockCopy(buff, headerLen, payload, 0, payload.Length);
                    AddCandidate(candidates, payload, 0, $"strip_{headerLen}");
                }
            }

            return candidates;
        }

        private bool TryReadVarint32(byte[] data, int offset, out int value, out int bytesRead)
        {
            value = 0;
            bytesRead = 0;
            int shift = 0;
            for (int i = 0; i < 5 && offset + i < data.Length; i++)
            {
                byte b = data[offset + i];
                value |= (b & 0x7F) << shift;
                bytesRead++;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            value = 0;
            bytesRead = 0;
            return false;
        }

        private void TryLogReadableSegments(byte[] buff)
        {
            try
            {
                if (buff == null || buff.Length < 16) return;
                var text = Encoding.UTF8.GetString(buff);
                var cleaned = new string(text.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());
                if (cleaned.Contains("正在看") || cleaned.Contains("评论") || cleaned.Contains("送出") || cleaned.Contains("进入直播间"))
                {
                    var preview = cleaned.Length > 120 ? cleaned.Substring(0, 120) : cleaned;
                    Logger.LogInfo($"[快手][可读片段] {preview}");
                }
            }
            catch
            {
                // ignore
            }
        }

        private void LogKuaishouPacketSignature(byte[] buff, string hostName)
        {
            try
            {
                var hexLen = Math.Min(64, buff.Length);
                var hex = BitConverter.ToString(buff, 0, hexLen).Replace("-", " ");
                Logger.LogInfo($"[快手][包特征] Host={hostName} Len={buff.Length} First16={hex}");

                // 可打印字符抽样，辅助识别是否有明文JSON/关键词
                var printable = new string(buff.Take(Math.Min(120, buff.Length))
                    .Select(b => (b >= 32 && b <= 126) ? (char)b : '.')
                    .ToArray());
                Logger.LogInfo($"[快手][包特征] PrintableHead={printable}");
            }
            catch
            {
                // ignore
            }
        }

        // 处理快手 Protobuf 数据
        private void ProcessKuaishouProtobuf(byte[] buff, string processName)
        {
            var envelope = Serializer.Deserialize<Modles.ProtoEntity.KsSocketMessage>(new ReadOnlyMemory<byte>(buff));
            if (envelope?.Payload == null)
            {
                Logger.LogInfo($"[快手] KsSocketMessage 解析失败或Payload为空");
                return;
            }

            var ksPayload = Serializer.Deserialize<Modles.ProtoEntity.KsPayload>(new ReadOnlyMemory<byte>(envelope.Payload));
            if (ksPayload?.SendMessages == null)
            {
                Logger.LogInfo($"[快手] KsPayload.SendMessages 为空");
                return;
            }

            Logger.LogInfo($"[快手] Protobuf解析成功，消息数={ksPayload.SendMessages.Count}");

            foreach (var sendMsg in ksPayload.SendMessages)
            {
                var msgType = (sendMsg.MsgType ?? "").ToUpper();
                var payload = sendMsg.Payload;
                if (payload == null) continue;

                switch (msgType)
                {
                    case "CHAT":
                        var chatMsg = Serializer.Deserialize<Modles.ProtoEntity.KsChatMessage>(new ReadOnlyMemory<byte>(payload));
                        FireKuaishouChat(chatMsg);
                        break;
                    case "GIFT":
                        var giftMsg = Serializer.Deserialize<Modles.ProtoEntity.KsGiftMessage>(new ReadOnlyMemory<byte>(payload));
                        FireKuaishouGift(giftMsg);
                        break;
                    case "LIKE":
                        var likeMsg = Serializer.Deserialize<Modles.ProtoEntity.KsLikeMessage>(new ReadOnlyMemory<byte>(payload));
                        FireKuaishouLike(likeMsg);
                        break;
                    case "ENTER":
                        var enterMsg = Serializer.Deserialize<Modles.ProtoEntity.KsEnterMessage>(new ReadOnlyMemory<byte>(payload));
                        FireKuaishouEnter(enterMsg);
                        break;
                    case "FOLLOW":
                        var followMsg = Serializer.Deserialize<Modles.ProtoEntity.KsFollowMessage>(new ReadOnlyMemory<byte>(payload));
                        FireKuaishouFollow(followMsg);
                        break;
                }
            }
        }

        // 处理快手 JSON 数据
        private void ProcessKuaishouJson(byte[] buff, string processName)
        {
            var json = Encoding.UTF8.GetString(buff);
            var jobj = JObject.Parse(json);
            var type = jobj["type"]?.Value<string>()?.ToUpper() ?? "";

            // 心跳 ACK 不处理
            if (type == "HEARTBEAT_ACK" || type == "HEARTBEAT") return;

            // 单条推送消息
            if (jobj["data"] is JObject singleData)
            {
                DispatchKuaishouJsonMsg(type, singleData);
                return;
            }

            // 批量推送
            if (jobj["sendMessages"] is JArray sendMsgs)
            {
                foreach (var item in sendMsgs)
                {
                    var msgType = item["msgType"]?.Value<string>()?.ToUpper() ?? "";
                    var data = item["payload"] as JObject;
                    if (data != null) DispatchKuaishouJsonMsg(msgType, data);
                }
            }
        }

        // 分发快手 JSON 消息
        private void DispatchKuaishouJsonMsg(string msgType, JObject data)
        {
            switch (msgType)
            {
                case "CHAT":
                    var chatMsg = new Modles.ProtoEntity.KsChatMessage
                    {
                        User = ParseKuaishouUser(data["user"]),
                        Content = data["content"]?.Value<string>() ?? ""
                    };
                    FireKuaishouChat(chatMsg);
                    break;
                case "GIFT":
                    var giftMsg = new Modles.ProtoEntity.KsGiftMessage
                    {
                        User = ParseKuaishouUser(data["user"]),
                        GiftId = data["giftId"]?.Value<long>() ?? 0,
                        GiftName = data["giftName"]?.Value<string>() ?? "",
                        Count = data["count"]?.Value<long>() ?? 1
                    };
                    FireKuaishouGift(giftMsg);
                    break;
                case "LIKE":
                    var likeMsg = new Modles.ProtoEntity.KsLikeMessage
                    {
                        User = ParseKuaishouUser(data["user"]),
                        Count = data["count"]?.Value<long>() ?? 1
                    };
                    FireKuaishouLike(likeMsg);
                    break;
                case "ENTER":
                    var enterMsg = new Modles.ProtoEntity.KsEnterMessage
                    {
                        User = ParseKuaishouUser(data["user"])
                    };
                    FireKuaishouEnter(enterMsg);
                    break;
            }
        }

        // 解析快手用户信息
        private Modles.ProtoEntity.KsUser ParseKuaishouUser(JToken userToken)
        {
            if (userToken == null) return new Modles.ProtoEntity.KsUser();

            var jobj = userToken as JObject;
            if (jobj == null) return new Modles.ProtoEntity.KsUser();

            return new Modles.ProtoEntity.KsUser
            {
                UserId = jobj["userId"]?.Value<string>() ?? "",
                Nickname = jobj["nickname"]?.Value<string>() ?? "",
                HeadUrl = jobj["headUrl"]?.Value<string>() ?? ""
            };
        }

        // 触发快手弹幕事件（转发给游戏）
        private void FireKuaishouChat(Modles.ProtoEntity.KsChatMessage msg)
        {
            Logger.LogInfo($"[快手][弹幕] {msg.User?.Nickname ?? "快手用户"}: {msg.Content ?? ""}");
            var data = new JObject
            {
                ["Content"] = msg.Content ?? "",
                ["User"] = new JObject
                {
                    ["Nickname"] = msg.User?.Nickname ?? "快手用户",
                    ["HeadImgUrl"] = msg.User?.HeadUrl ?? "",
                    ["SecUid"] = msg.User?.UserId ?? ""
                }
            };
            var pack = JsonEntity.BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), JsonEntity.PackMsgType.弹幕消息);
            this.OnKuaishouProxyBarrage?.Invoke(this, new KsBarragePusher.BarrageEventArgs(pack));
        }

        private void FireKuaishouGift(Modles.ProtoEntity.KsGiftMessage msg)
        {
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 送出 {msg.GiftName} x {msg.Count}",
                ["User"] = new JObject
                {
                    ["Nickname"] = msg.User?.Nickname ?? "",
                    ["HeadImgUrl"] = msg.User?.HeadUrl ?? "",
                    ["SecUid"] = msg.User?.UserId ?? ""
                },
                ["GiftId"] = msg.GiftId,
                ["GiftName"] = msg.GiftName ?? "",
                ["Count"] = msg.Count
            };
            var pack = JsonEntity.BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), JsonEntity.PackMsgType.礼物消息);
            this.OnKuaishouProxyBarrage?.Invoke(this, new KsBarragePusher.BarrageEventArgs(pack));
        }

        private void FireKuaishouLike(Modles.ProtoEntity.KsLikeMessage msg)
        {
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 点赞 x {msg.Count}",
                ["User"] = new JObject
                {
                    ["Nickname"] = msg.User?.Nickname ?? "",
                    ["HeadImgUrl"] = msg.User?.HeadUrl ?? "",
                    ["SecUid"] = msg.User?.UserId ?? ""
                },
                ["Count"] = msg.Count
            };
            var pack = JsonEntity.BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), JsonEntity.PackMsgType.点赞消息);
            this.OnKuaishouProxyBarrage?.Invoke(this, new KsBarragePusher.BarrageEventArgs(pack));
        }

        private void FireKuaishouEnter(Modles.ProtoEntity.KsEnterMessage msg)
        {
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 来了",
                ["User"] = new JObject
                {
                    ["Nickname"] = msg.User?.Nickname ?? "",
                    ["HeadImgUrl"] = msg.User?.HeadUrl ?? "",
                    ["SecUid"] = msg.User?.UserId ?? ""
                }
            };
            var pack = JsonEntity.BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), JsonEntity.PackMsgType.进直播间);
            this.OnKuaishouProxyBarrage?.Invoke(this, new KsBarragePusher.BarrageEventArgs(pack));
        }

        private void FireKuaishouFollow(Modles.ProtoEntity.KsFollowMessage msg)
        {
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 关注了主播",
                ["User"] = new JObject
                {
                    ["Nickname"] = msg.User?.Nickname ?? "",
                    ["HeadImgUrl"] = msg.User?.HeadUrl ?? "",
                    ["SecUid"] = msg.User?.UserId ?? ""
                }
            };
            var pack = JsonEntity.BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), JsonEntity.PackMsgType.关注消息);
            this.OnKuaishouProxyBarrage?.Invoke(this, new KsBarragePusher.BarrageEventArgs(pack));
        }

        //http 数据处理
        private void Proxy_OnFetchResponse(object sender, HttpResponseEventArgs e)
        {
            var payload = e.Payload;
            if (payload == null || payload.Length == 0) return;

            DumpKuaishouRawBytes("http_any_raw", e.HostName ?? e.ProcessName, payload);
            if (IsUnknownFlowHost(e.HostName))
            {
                DumpKuaishouRawBytes("http_unknown_host_raw", e.HostName ?? e.ProcessName, payload);
            }
            if (IsLikelyKuaishouHttpEvent(e))
            {
                RecordKuaishouFlow("http", e.HostName, e.RequestUri, e.ProcessName, payload.Length, string.Empty, TryDecodeFlowHintText(payload));
                TrackKsHttpHost(e.HostName, e.ProcessName, payload.Length);
                DumpKuaishouRawBytes("http_raw", e.HostName ?? e.ProcessName, payload);
                TryLogKuaishouHttpPreflight(e, payload);
                return;
            }

            try
            {
                var response = Serializer.Deserialize<Response>(new ReadOnlyMemory<byte>(payload));
                response?.Messages?.ForEach(f => DoMessage(f, e.ProcessName));
            }
            catch
            {
                // 非抖音protobuf响应忽略
            }
        }

        private bool IsLikelyKuaishouHttpEvent(HttpResponseEventArgs e)
        {
            var host = (e?.HostName ?? string.Empty).ToLowerInvariant();
            var process = (e?.ProcessName ?? string.Empty).ToLowerInvariant();
            var hostHit = host.Contains("kuaishou") || host.Contains("wsukwai") || host.Contains("gifshow");
            var processHit = process.Contains("kwailive") || process.Contains("kuaishou") || process.Contains("gifshow") || process.Contains("kscloud");
            return hostHit || processHit;
        }

        private void TryLogKuaishouHttpPreflight(HttpResponseEventArgs e, byte[] payload)
        {
            if (e == null || payload == null || payload.Length == 0) return;
            try
            {
                var uri = e.RequestUri ?? string.Empty;
                var host = (e.HostName ?? string.Empty).ToLowerInvariant();
                var process = (e.ProcessName ?? string.Empty).ToLowerInvariant();
                if (!IsLikelyKuaishouPreflightUri(host, uri)) return;

                DumpKuaishouRawBytes("http_prefetch_raw", e.HostName ?? e.ProcessName, payload);

                var text = Encoding.UTF8.GetString(payload);
                var hits = ExtractKuaishouPreflightHints(text, uri);
                var shortUri = NormalizeFlowPath(uri);
                var focusKey = $"{host}|{shortUri}";
                if (!ShouldLogKsHttpFocus(focusKey, 2))
                {
                    return;
                }

                if (IsKuaishouWlogNoise(host, uri))
                {
                    Logger.LogInfo($"[KS_HTTP_PREFLIGHT_ROUTE] level=NOISE host={e.HostName} process={e.ProcessName} uri={uri} len={payload.Length} hits={hits}");
                    return;
                }

                var method = e.HttpClient?.Request?.Method?.ToString() ?? "UNKNOWN";
                var status = (int?)(e.HttpClient?.Response?.StatusCode) ?? -1;
                var location = e.HttpClient?.Response?.Headers?.GetFirstHeader("Location")?.Value ?? string.Empty;
                var decodedLocation = SafeUrlDecode(location);
                var ext = Path.GetExtension(shortUri ?? string.Empty)?.ToLowerInvariant() ?? string.Empty;
                var isStaticAsset = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".ico";
                var isTextLike = LooksLikeTextPayload(payload);
                if (isStaticAsset && !isTextLike)
                {
                    Logger.LogInfo($"[KS_HTTP_ASSET_SKIP] host={e.HostName} process={e.ProcessName} uri={uri} ext={ext} len={payload.Length}");
                    return;
                }
                Logger.LogInfo($"[KS_HTTP_REQ_CANDIDATE] method={method} host={e.HostName} process={e.ProcessName} uri={uri}");
                Logger.LogInfo($"[KS_HTTP_RESP_CANDIDATE] status={status} host={e.HostName} process={e.ProcessName} uri={uri} location={decodedLocation} hits={hits}");
                TryLogKsDomainClusterEvidence(e, method, status, uri);
                if (isTextLike)
                {
                    var preview = BuildKsHttpTextPreview(payload, 320);
                    Logger.LogInfo($"[KS_HTTP_TEXT_CANDIDATE] host={e.HostName} process={e.ProcessName} uri={uri} preview={preview}");
                }
                if (!string.IsNullOrWhiteSpace(decodedLocation)
                    && decodedLocation.IndexOf("live.kuaishou.com/u/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.LogInfo($"[KS_URL_U_HIT] channel=http_location host={e.HostName} process={e.ProcessName} url={decodedLocation}");
                }

                if (process.Contains("kwailive"))
                {
                    DumpKuaishouRawBytes("http_prefetch_non_wlog_raw", e.HostName ?? e.ProcessName, payload);
                    Logger.LogInfo($"[KS_HTTP_PREFLIGHT_ROUTE] level=NON_WLOG host={e.HostName} process={e.ProcessName} uri={uri} len={payload.Length} hits={hits}");
                }

                if (IsStrongKuaishouPreflightCandidate(host, uri))
                {
                    DumpKuaishouRawBytes("http_prefetch_strong_raw", e.HostName ?? e.ProcessName, payload);
                    Logger.LogInfo($"[KS_HTTP_PREFLIGHT_ROUTE] level=STRONG host={e.HostName} process={e.ProcessName} uri={uri} len={payload.Length} hits={hits}");
                    return;
                }

                Logger.LogInfo($"[KS_HTTP_PREFLIGHT_ROUTE] level=CANDIDATE host={e.HostName} process={e.ProcessName} uri={uri} len={payload.Length} hits={hits}");
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_HTTP_PREFLIGHT] failed: {ex.Message}");
            }
        }

        private bool ShouldLogKsHttpFocus(string key, int minSeconds)
        {
            var now = DateTime.Now;
            lock (ksFlowLock)
            {
                if (ksHttpFocusLastLogAt.TryGetValue(key, out var lastAt) && (now - lastAt).TotalSeconds < minSeconds)
                {
                    return false;
                }
                ksHttpFocusLastLogAt[key] = now;
            }
            return true;
        }

        private void TrackKsHttpHost(string host, string processName, int payloadLength)
        {
            try
            {
                var h = NormalizeFlowHost(host);
                var p = (processName ?? string.Empty).Trim().ToLowerInvariant();
                lock (ksFlowLock)
                {
                    if (!ksHttpHostStats.TryGetValue(h, out var stat))
                    {
                        stat = new KsHttpHostStat { Host = h, Process = p, FirstSeen = DateTime.Now };
                        ksHttpHostStats[h] = stat;
                    }
                    stat.Count++;
                    stat.TotalBytes += Math.Max(0, payloadLength);
                    stat.LastSeen = DateTime.Now;
                    if (!string.IsNullOrWhiteSpace(p)) stat.Process = p;

                    var now = DateTime.Now;
                    if ((now - ksHttpHostLastEmitAt).TotalSeconds < 20) return;
                    ksHttpHostLastEmitAt = now;

                    var top = ksHttpHostStats.Values
                        .Where(s => (now - s.LastSeen).TotalMinutes <= 5)
                        .OrderByDescending(s => s.TotalBytes)
                        .Take(10)
                        .ToList();
                    if (top.Count == 0) return;
                    Logger.LogInfo("[KS_HTTP_HOST_TOP] ===== kwailive HTTP hosts top =====");
                    foreach (var s in top)
                    {
                        var nonWlog = s.Host.IndexOf("wlog.gifshow.com", StringComparison.OrdinalIgnoreCase) < 0;
                        Logger.LogInfo($"[KS_HTTP_HOST_TOP] host={s.Host} process={s.Process} count={s.Count} bytes={s.TotalBytes} nonWlog={nonWlog}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_HTTP_HOST_TOP] failed: {ex.Message}");
            }
        }

        private bool LooksLikeTextPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return false;
            var sampleLen = Math.Min(payload.Length, 256);
            var printable = 0;
            for (var i = 0; i < sampleLen; i++)
            {
                var b = payload[i];
                if (b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126)) printable++;
            }
            return sampleLen > 0 && printable * 100 / sampleLen >= 70;
        }

        private string BuildKsHttpTextPreview(byte[] payload, int maxLen)
        {
            if (payload == null || payload.Length == 0) return string.Empty;
            try
            {
                var text = Encoding.UTF8.GetString(payload);
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                var safe = new string(text.Take(maxLen).Select(c => char.IsControl(c) ? '.' : c).ToArray());
                return safe;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsLikelyKuaishouPreflightUri(string host, string uri)
        {
            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(uri)) return false;
            var h = (host ?? string.Empty).ToLowerInvariant();
            var u = (uri ?? string.Empty).ToLowerInvariant();
            var hostHit = h.Contains("kuaishou") || h.Contains("wsukwai") || h.Contains("gifshow");
            if (!hostHit) return false;
            if (IsKnownKsControlHost(h)) return true;
            return u.Contains("/rest/") || u.Contains("/graphql") || u.Contains("live") || u.Contains("room") || u.Contains("stream") || u.Contains("webcast");
        }

        private bool IsKuaishouWlogNoise(string host, string uri)
        {
            var h = (host ?? string.Empty).ToLowerInvariant();
            var u = (uri ?? string.Empty).ToLowerInvariant();
            return h.Contains("wlog.gifshow.com") || u.Contains("/rest/kd/log/collect");
        }

        private bool IsStrongKuaishouPreflightCandidate(string host, string uri)
        {
            var h = (host ?? string.Empty).ToLowerInvariant();
            var u = (uri ?? string.Empty).ToLowerInvariant();
            if (IsKuaishouWlogNoise(h, u)) return false;
            if (!(h.Contains("kuaishou") || h.Contains("wsukwai") || h.Contains("gifshow"))) return false;
            if (IsKnownKsControlHost(h)) return true;

            return u.Contains("/graphql")
                || u.Contains("/webcast")
                || u.Contains("/live/")
                || u.Contains("/room/")
                || u.Contains("/stream/")
                || u.Contains("/feed/")
                || u.Contains("/pull");
        }

        private void TryLogKsDomainClusterEvidence(HttpResponseEventArgs e, string method, int status, string uri)
        {
            if (e == null) return;
            try
            {
                var host = NormalizeFlowHost(e.HostName);
                var process = (e.ProcessName ?? string.Empty).ToLowerInvariant();
                var path = NormalizeFlowPath(uri);
                var cluster = ResolveKsDomainCluster(host);
                if (cluster == "OTHER") return;

                var score = 0;
                switch (cluster)
                {
                    case "KSAPISRV_EDGE":
                    case "GIFSHOW_EDGE":
                        score += 30;
                        break;
                    case "RTC_REPORT":
                        score += 22;
                        break;
                    case "KUAISHOU_CORE":
                        score += 18;
                        break;
                }
                if (IsKnownKsControlHost(host)) score += 18;
                if (process.Contains("kwailive")) score += 10;
                if (status >= 200 && status < 400) score += 6;
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                    || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
                {
                    score += 8;
                }
                if (path.Contains("/api/") || path.Contains("/rest/") || path.Contains("/live/") || path.Contains("/room/") || path.Contains("/stream/"))
                {
                    score += 16;
                }
                if (path.Contains("/rest/kd/log/collect")) score -= 50;
                if (score < 0) score = 0;

                var now = DateTime.Now;
                lock (ksFlowLock)
                {
                    if (!ksDomainClusterStats.TryGetValue(cluster, out var stat))
                    {
                        stat = new KsDomainClusterStat
                        {
                            Cluster = cluster,
                            FirstSeen = now,
                            LastSeen = now
                        };
                        ksDomainClusterStats[cluster] = stat;
                    }

                    stat.Hits++;
                    stat.LastSeen = now;
                    stat.AccScore += score;
                    if (score > stat.MaxScore) stat.MaxScore = score;
                    if (!string.IsNullOrWhiteSpace(host)) stat.LastHost = host;
                    if (!string.IsNullOrWhiteSpace(path)) stat.LastPath = path;
                }

                var clusterKey = $"{cluster}|{host}|{path}";
                if (ShouldLogKsDomainCluster(clusterKey, 3))
                {
                    Logger.LogInfo($"[KS_DOMAIN_CLUSTER] cluster={cluster} host={host} method={method} status={status} score={score} knownControl={IsKnownKsControlHost(host)} path={path}");
                }

                EmitKsDomainClusterState();
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_DOMAIN_CLUSTER] failed: {ex.Message}");
            }
        }

        private bool ShouldLogKsDomainCluster(string key, int minSeconds)
        {
            var now = DateTime.Now;
            lock (ksFlowLock)
            {
                if (ksDomainClusterLastLogAt.TryGetValue(key, out var lastAt) && (now - lastAt).TotalSeconds < minSeconds)
                {
                    return false;
                }
                ksDomainClusterLastLogAt[key] = now;
            }
            return true;
        }

        private string ResolveKsDomainCluster(string host)
        {
            var h = (host ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(h)) return "OTHER";
            if (h.Contains("report-rtc-mainapp.kuaishou.com")) return "RTC_REPORT";
            if (h.EndsWith(".ksapisrv.com") || h.Contains("ksapisrv.com")) return "KSAPISRV_EDGE";
            if (h.EndsWith(".gifshow.com") || h.Contains("gifshow.com")) return "GIFSHOW_EDGE";
            if (h.EndsWith(".kuaishou.com") || h.Contains("kuaishou.com")) return "KUAISHOU_CORE";
            return "OTHER";
        }

        private bool IsKnownKsControlHost(string host)
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

        private void EmitKsDomainClusterState()
        {
            var now = DateTime.Now;
            List<KsDomainClusterStat> active;
            lock (ksFlowLock)
            {
                active = ksDomainClusterStats.Values
                    .Where(s => (now - s.LastSeen).TotalMinutes <= 3)
                    .OrderByDescending(s => s.AccScore)
                    .Take(6)
                    .Select(s => new KsDomainClusterStat
                    {
                        Cluster = s.Cluster,
                        Hits = s.Hits,
                        AccScore = s.AccScore,
                        MaxScore = s.MaxScore,
                        FirstSeen = s.FirstSeen,
                        LastSeen = s.LastSeen,
                        LastHost = s.LastHost,
                        LastPath = s.LastPath
                    })
                    .ToList();
            }
            if (active.Count == 0) return;

            var hasApiEdge = active.Any(s => s.Cluster == "KSAPISRV_EDGE" || s.Cluster == "GIFSHOW_EDGE");
            var hasRtc = active.Any(s => s.Cluster == "RTC_REPORT");
            var totalScore = active.Sum(s => s.AccScore);
            var level = "PROBE";
            if (totalScore >= 180 || hasApiEdge) level = "CANDIDATE";
            if (hasApiEdge && hasRtc && totalScore >= 260) level = "CONFIRMED";

            var top = active.Take(3)
                .Select(s => $"{s.Cluster}:{s.Hits}/{s.AccScore}")
                .ToArray();
            Logger.LogInfo($"[KS_DOMAIN_CLUSTER_STATE] level={level} totalScore={totalScore} hasApiEdge={hasApiEdge} hasRtc={hasRtc} top={string.Join("|", top)}");
        }

        private string ExtractKuaishouPreflightHints(string text, string uri)
        {
            if (string.IsNullOrWhiteSpace(text)) return $"uri={uri}";
            var patterns = new[]
            {
                @"authorId[=:\""\\s]{0,6}[A-Za-z0-9_\-]{4,32}",
                @"liveStreamId[=:\""\\s]{0,6}[A-Za-z0-9_\-]{4,64}",
                @"roomId[=:\""\\s]{0,6}[A-Za-z0-9_\-]{3,32}",
                @"nickname[=:\""\\s]{0,6}[^\\""\\r\\n]{2,48}",
                @"title[=:\""\\s]{0,6}[^\\""\\r\\n]{2,80}",
                @"\bstatus[=:\""\\s]{0,6}[A-Za-z0-9_\-]{1,16}",
                @"\blive[=:\""\\s]{0,6}(true|false|0|1)"
            };
            var hits = patterns
                .SelectMany(p => Regex.Matches(text, p, RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();
            if (hits.Count == 0)
            {
                return $"uri={uri}; no-key-hit";
            }
            return $"uri={uri}; {string.Join(" | ", hits)}";
        }

        private bool IsUnknownFlowHost(string host)
        {
            var h = (host ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(h)) return true;
            if (h.StartsWith("ksraw:", StringComparison.OrdinalIgnoreCase)) return true;
            return Regex.IsMatch(h, @"^\d{1,3}(\.\d{1,3}){3}$");
        }

        private void RecordKuaishouFlow(string protocol, string host, string uri, string processName, int payloadLength, string contentType, string hintText)
        {
            try
            {
                var flowHost = NormalizeFlowHost(host);
                var flowPath = NormalizeFlowPath(uri);
                var hintHits = CountFlowHintHits(hintText);
                var signalSummary = ExtractWsRouteSignalSummary(hintText);
                var score = ScoreKuaishouFlow(protocol, flowHost, flowPath, uri, processName, payloadLength, hintHits);
                Logger.LogInfo($"[KS_FLOW_LEDGER] proto={protocol} process={processName} host={flowHost} path={flowPath} len={payloadLength} score={score} ct={contentType}");
                Logger.LogInfo($"[KS_FLOW_SCORE] score={score} level={(score >= 80 ? "strong" : (score >= 40 ? "medium" : "weak"))} proto={protocol} host={flowHost} path={flowPath} hintHits={hintHits} uri={uri}");
                if (protocol.Equals("ws", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(flowHost, @"^\d{1,3}(\.\d{1,3}){3}$")
                    && score >= 50)
                {
                    Logger.LogInfo($"[KS_WS_ROUTE_CANDIDATE] host={flowHost} score={score} hintHits={hintHits} len={payloadLength} process={processName}");
                    if (!string.IsNullOrWhiteSpace(signalSummary))
                    {
                        Logger.LogInfo($"[KS_WS_ROUTE_SIGNAL] host={flowHost} score={score} signals={signalSummary}");
                    }
                    TryLogKsLiveRouteState(flowHost, score, signalSummary, payloadLength, processName);
                }

                var clusterKey = $"{protocol}|{flowHost}|{flowPath}";
                lock (ksFlowLock)
                {
                    if (!ksFlowClusters.TryGetValue(clusterKey, out var stat))
                    {
                        stat = new KsFlowClusterStat
                        {
                            Protocol = protocol,
                            Host = flowHost,
                            Path = flowPath,
                            FirstSeen = DateTime.Now
                        };
                        ksFlowClusters[clusterKey] = stat;
                    }
                    stat.Count++;
                    stat.LastSeen = DateTime.Now;
                    stat.TotalBytes += Math.Max(0, payloadLength);
                    if (score > stat.MaxScore) stat.MaxScore = score;
                    if (protocol.Equals("ws", StringComparison.OrdinalIgnoreCase)
                        && Regex.IsMatch(flowHost, @"^\d{1,3}(\.\d{1,3}){3}$")
                        && score >= 50)
                    {
                        stat.WsCandidateHits++;
                    }

                    if (stat.Count == 1 || stat.Count % 20 == 0)
                    {
                        Logger.LogInfo($"[KS_FLOW_CLUSTER] key={clusterKey} count={stat.Count} totalBytes={stat.TotalBytes} maxScore={stat.MaxScore} first={stat.FirstSeen:HH:mm:ss} last={stat.LastSeen:HH:mm:ss}");
                    }
                    if (stat.WsCandidateHits > 0 && (stat.WsCandidateHits == 1 || stat.WsCandidateHits % 5 == 0))
                    {
                        Logger.LogInfo($"[KS_WS_ROUTE_TOP] host={flowHost} candidateHits={stat.WsCandidateHits} count={stat.Count} totalBytes={stat.TotalBytes} maxScore={stat.MaxScore}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_FLOW_LEDGER] failed: {ex.Message}");
            }
        }

        private string NormalizeFlowHost(string host)
        {
            var h = (host ?? string.Empty).Trim().ToLowerInvariant();
            if (h.StartsWith("ksraw:", StringComparison.OrdinalIgnoreCase))
            {
                h = h.Substring("ksraw:".Length);
            }
            return string.IsNullOrWhiteSpace(h) ? "unknown-host" : h;
        }

        private string NormalizeFlowPath(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return "/";
            try
            {
                var parsed = new Uri(uri);
                var p = parsed.AbsolutePath?.ToLowerInvariant();
                return string.IsNullOrWhiteSpace(p) ? "/" : p;
            }
            catch
            {
                return "/";
            }
        }

        private string TryDecodeFlowHintText(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return string.Empty;
            try
            {
                var text = Encoding.UTF8.GetString(payload);
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                return text.Length > 6000 ? text.Substring(0, 6000) : text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private int CountFlowHintHits(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            try
            {
                var patterns = new[]
                {
                    "authorId",
                    "liveStreamId",
                    "roomId",
                    "nickname",
                    "\"title\"",
                    "target_live_stream_id",
                    "/live/",
                    "platformBiz",
                    "gzonePcMate",
                    "author_label",
                    "人在看"
                };
                return patterns.Count(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return 0;
            }
        }

        private string ExtractWsRouteSignalSummary(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var signals = new List<string>();

            if (text.IndexOf("platformBiz", StringComparison.OrdinalIgnoreCase) >= 0) signals.Add("platformBiz");
            if (text.IndexOf("gzonePcMate", StringComparison.OrdinalIgnoreCase) >= 0) signals.Add("gzonePcMate");
            if (text.IndexOf("author_label", StringComparison.OrdinalIgnoreCase) >= 0) signals.Add("author_label");
            if (text.IndexOf("人在看", StringComparison.OrdinalIgnoreCase) >= 0) signals.Add("人在看");

            var nameMatches = Regex.Matches(text, @"[\u4e00-\u9fa5]{2,8}");
            foreach (Match m in nameMatches)
            {
                var v = m.Value;
                if (v.Contains("人在看")) continue;
                if (signals.Contains(v)) continue;
                signals.Add(v);
                if (signals.Count >= 8) break;
            }

            return signals.Count == 0 ? string.Empty : string.Join("|", signals.Take(8));
        }

        private void TryLogKsLiveRouteState(string host, int score, string signalSummary, int payloadLength, string processName)
        {
            try
            {
                var now = DateTime.Now;
                lock (ksFlowLock)
                {
                    if (ksRouteStateLastLogAt.TryGetValue(host, out var lastAt) && (now - lastAt).TotalSeconds < 3)
                    {
                        return;
                    }
                    ksRouteStateLastLogAt[host] = now;
                }

                var signals = (signalSummary ?? string.Empty).ToLowerInvariant();
                var hasAudience = signals.Contains("人在看");
                var hasRouteMarkers = signals.Contains("platformbiz") || signals.Contains("author_label") || signals.Contains("gzonepcmate");
                var level = "PROBE";
                if (score >= 70 || hasRouteMarkers) level = "CANDIDATE";
                if (score >= 85 && hasAudience && hasRouteMarkers) level = "CONFIRMED";

                Logger.LogInfo($"[KS_LIVE_ROUTE_STATE] level={level} host={host} score={score} payload={payloadLength} process={processName} signals={signalSummary}");
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_LIVE_ROUTE_STATE] failed: {ex.Message}");
            }
        }

        private int ScoreKuaishouFlow(string protocol, string host, string path, string uri, string processName, int payloadLength, int hintHits)
        {
            var score = 0;
            var proto = (protocol ?? string.Empty).ToLowerInvariant();
            var h = (host ?? string.Empty).ToLowerInvariant();
            var p = (path ?? string.Empty).ToLowerInvariant();
            var u = (uri ?? string.Empty).ToLowerInvariant();
            var process = (processName ?? string.Empty).ToLowerInvariant();
            var isIpv4Host = Regex.IsMatch(h, @"^\d{1,3}(\.\d{1,3}){3}$");

            if (h.Contains("kuaishou") || h.Contains("gifshow") || h.Contains("wsukwai")) score += 20;
            if (p.Contains("live") || p.Contains("room") || p.Contains("stream")) score += 25;
            if (p.Contains("webcast") || p.Contains("graphql") || p.Contains("feed") || p.Contains("pull")) score += 35;
            if (u.Contains("authorid") || u.Contains("livestreamid") || u.Contains("roomid")) score += 20;
            if (process.Contains("kwailive")) score += 10;
            if (proto == "ws" && isIpv4Host) score += 30;
            if (proto == "ws" && payloadLength >= 300 && payloadLength <= 4096) score += 10;
            if (hintHits > 0) score += Math.Min(40, hintHits * 12);
            if (h.Contains("wlog.gifshow.com") || p.Contains("/rest/kd/log/collect")) score -= 80;

            if (score < 0) score = 0;
            if (score > 100) score = 100;
            return score;
        }

        private class KsFlowClusterStat
        {
            public string Protocol { get; set; }
            public string Host { get; set; }
            public string Path { get; set; }
            public int Count { get; set; }
            public long TotalBytes { get; set; }
            public int MaxScore { get; set; }
            public int WsCandidateHits { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
        }

        private class KsHttpHostStat
        {
            public string Host { get; set; }
            public string Process { get; set; }
            public int Count { get; set; }
            public long TotalBytes { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
        }

        private class KsDomainClusterStat
        {
            public string Cluster { get; set; }
            public int Hits { get; set; }
            public int AccScore { get; set; }
            public int MaxScore { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public string LastHost { get; set; }
            public string LastPath { get; set; }
        }

        private void DumpKuaishouRawBytes(string channel, string hostOrTag, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "ks_raw");
                Directory.CreateDirectory(root);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var safeTag = SanitizeFilePart(hostOrTag);
                var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                var fileBase = $"{ts}_{channel}_{safeTag}_{data.Length}_{suffix}";
                var binPath = Path.Combine(root, fileBase + ".bin");
                File.WriteAllBytes(binPath, data);

                var text = Encoding.UTF8.GetString(data);
                var txtPath = Path.Combine(root, fileBase + ".txt");
                File.WriteAllText(txtPath, text, Encoding.UTF8);
                var b64Path = Path.Combine(root, fileBase + ".b64.txt");
                File.WriteAllText(b64Path, Convert.ToBase64String(data), Encoding.ASCII);
                var hexPath = Path.Combine(root, fileBase + ".hex.txt");
                File.WriteAllText(hexPath, BitConverter.ToString(data).Replace("-", string.Empty), Encoding.ASCII);
                Logger.LogInfo($"[KS_RAW_DUMP] channel={channel} tag={safeTag} len={data.Length} file={fileBase}");
                LogKuaishouRawIndex(channel, fileBase, text);
                LogKuaishouRawAllText(channel, fileBase, text);
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_RAW_DUMP] failed channel={channel}: {ex.Message}");
            }
        }

        private void LogKuaishouRawIndex(string channel, string fileBase, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var patterns = new[]
                {
                    @"authorId[=:\""\\s]{0,6}[A-Za-z0-9_\-]{4,32}",
                    @"liveStreamId[=:\""\\s]{0,6}[A-Za-z0-9_\-]{4,64}",
                    @"nickname[=:\""\\s]{0,6}[^\\""\\r\\n]{2,48}",
                    @"title[=:\""\\s]{0,6}[^\\""\\r\\n]{2,80}",
                    @"/u/[A-Za-z0-9_\-]{4,32}",
                    @"profile/[A-Za-z0-9_\-]{4,32}"
                };
                var hits = patterns
                    .SelectMany(p => Regex.Matches(text, p, RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList();
                if (hits.Count == 0) return;
                Logger.LogInfo($"[KS_RAW_INDEX] channel={channel} file={fileBase} hits={string.Join(" | ", hits)}");
            }
            catch
            {
                // ignore
            }
        }

        private void LogKuaishouRawAllText(string channel, string fileBase, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var preview = new string(text.Take(2000).Select(c => char.IsControl(c) ? '.' : c).ToArray());
                Logger.LogInfo($"[KS_RAW_ALL] channel={channel} file={fileBase} preview={preview}");

                var tokens = Regex.Matches(text, @"[\u4e00-\u9fa5A-Za-z0-9_/\-:=?&%.]{2,80}")
                    .Cast<Match>()
                    .Select(m => m.Value)
                    .Distinct()
                    .Take(400)
                    .ToList();
                if (tokens.Count == 0) return;

                for (int i = 0; i < tokens.Count; i += 40)
                {
                    var part = tokens.Skip(i).Take(40);
                    Logger.LogInfo($"[KS_RAW_ALL_TOKENS] channel={channel} file={fileBase} part={i / 40 + 1} tokens={string.Join(" | ", part)}");
                }

                TryLogDecodedKuaishouUrls(channel, fileBase, text);
                TryLogKuaishouPushConfigSignals(channel, fileBase, text);
            }
            catch
            {
                // ignore
            }
        }

        private void TryLogKuaishouPushConfigSignals(string channel, string fileBase, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var lower = text.ToLowerInvariant();
                var hasPushHints = lower.Contains("rtmp://")
                    || lower.Contains("srt://")
                    || lower.Contains("webrtc://")
                    || lower.Contains("streamkey")
                    || lower.Contains("stream_key")
                    || lower.Contains("pushurl")
                    || lower.Contains("push_url")
                    || lower.Contains("publishurl")
                    || lower.Contains("publish_url")
                    || lower.Contains("live-voip.com")
                    || lower.Contains("voip.live-voip.com")
                    || lower.Contains("/gifshow/")
                    || lower.Contains("ingest")
                    || lower.Contains("endpoint")
                    || lower.Contains("backup");
                if (!hasPushHints) return;

                var authorId = Regex.Match(text, @"authorId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,32})", RegexOptions.IgnoreCase).Groups[1].Value;
                var liveStreamId = Regex.Match(text, @"liveStreamId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})", RegexOptions.IgnoreCase).Groups[1].Value;
                var roomId = Regex.Match(text, @"roomId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})", RegexOptions.IgnoreCase).Groups[1].Value;
                var sessionId = Regex.Match(text, @"sessionId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})", RegexOptions.IgnoreCase).Groups[1].Value;
                var rtmp = Regex.Match(text, @"rtmp://[^\s\""']{12,800}", RegexOptions.IgnoreCase).Value;
                var ingest = Regex.Match(text, @"(?:srt|webrtc)://[^\s\""']{12,800}", RegexOptions.IgnoreCase).Value;
                var streamKey = Regex.Match(text, @"(?:streamKey|stream_key|pushKey|push_key)[=:\""\\s]{0,6}([A-Za-z0-9_\-]{6,256})", RegexOptions.IgnoreCase).Groups[1].Value;
                var publishUrl = Regex.Match(text, @"(?:publishUrl|publish_url|pushUrl|push_url)[=:\""\\s]{0,6}(https?://[^\s\""']{8,800}|rtmp://[^\s\""']{8,800})", RegexOptions.IgnoreCase).Groups[1].Value;

                var authHintCount = 0;
                if (lower.Contains("token")) authHintCount++;
                if (lower.Contains("sign")) authHintCount++;
                if (lower.Contains("signature")) authHintCount++;
                if (lower.Contains("nonce")) authHintCount++;
                if (lower.Contains("expire")) authHintCount++;
                if (lower.Contains("timestamp")) authHintCount++;
                if (!string.IsNullOrWhiteSpace(streamKey)) authHintCount += 2;

                var sessionHintCount = 0;
                if (!string.IsNullOrWhiteSpace(authorId)) sessionHintCount++;
                if (!string.IsNullOrWhiteSpace(liveStreamId)) sessionHintCount++;
                if (!string.IsNullOrWhiteSpace(roomId)) sessionHintCount++;
                if (!string.IsNullOrWhiteSpace(sessionId)) sessionHintCount++;

                var configHintCount = 0;
                if (lower.Contains("publishurl") || lower.Contains("publish_url")) configHintCount++;
                if (lower.Contains("pushurl") || lower.Contains("push_url")) configHintCount++;
                if (lower.Contains("server")) configHintCount++;
                if (lower.Contains("endpoint")) configHintCount++;
                if (lower.Contains("backup")) configHintCount++;

                var hasAddress = !string.IsNullOrWhiteSpace(rtmp) || !string.IsNullOrWhiteSpace(ingest) || !string.IsNullOrWhiteSpace(publishUrl);
                var hasAuth = authHintCount >= 2;
                var hasSession = sessionHintCount >= 1;
                var hasConfig = configHintCount >= 1;

                var hitKey = $"{channel}|{rtmp}|{ingest}|{publishUrl}|{streamKey}|{authorId}|{liveStreamId}|{roomId}";
                if (!ShouldLogKsPushHit(hitKey, 6)) return;

                var preview = text.Length > 420 ? text.Substring(0, 420) + "..." : text;
                preview = preview.Replace("\r", " ").Replace("\n", " ");
                Logger.LogInfo($"[KS_PUSH_CONFIG_CANDIDATE] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} roomId={roomId} hasAddress={hasAddress} hasAuth={hasAuth} hasSession={hasSession} hasConfig={hasConfig} preview={preview}");

                var isStrong = (hasAddress && hasAuth && hasSession) || (hasConfig && hasAuth && hasSession);
                if (isStrong)
                {
                    Logger.LogInfo($"[KS_PUSH_CONFIG_CANDIDATE_STRONG] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} roomId={roomId} authHints={authHintCount} sessionHints={sessionHintCount} configHints={configHintCount}");
                }

                if (!string.IsNullOrWhiteSpace(rtmp))
                {
                    Logger.LogInfo($"[KS_PUSH_URL_HIT] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} rtmp={rtmp}");
                }
                if (!string.IsNullOrWhiteSpace(ingest))
                {
                    Logger.LogInfo($"[KS_PUSH_URL_HIT] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} ingest={ingest}");
                }
                if (!string.IsNullOrWhiteSpace(publishUrl))
                {
                    Logger.LogInfo($"[KS_PUSH_URL_HIT] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} publishUrl={publishUrl}");
                }
                if (!string.IsNullOrWhiteSpace(streamKey))
                {
                    Logger.LogInfo($"[KS_STREAM_KEY_HIT] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} streamKey={streamKey}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_PUSH_CONFIG_CANDIDATE] failed: {ex.Message}");
            }
        }

        private bool ShouldLogKsPushHit(string key, int minSeconds)
        {
            var now = DateTime.Now;
            var k = (key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(k)) return false;
            lock (ksFlowLock)
            {
                if (ksPushHitLastLogAt.TryGetValue(k, out var lastAt) && (now - lastAt).TotalSeconds < minSeconds)
                {
                    return false;
                }
                ksPushHitLastLogAt[k] = now;
            }
            return true;
        }

        private void TryLogDecodedKuaishouUrls(string channel, string fileBase, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var urlCandidates = new List<string>();

                foreach (Match m in Regex.Matches(text, @"https?://[^\s""'|]{8,600}", RegexOptions.IgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(m.Value)) urlCandidates.Add(m.Value.Trim());
                }
                foreach (Match m in Regex.Matches(text, @"https?%3a%2f%2f[^\s""'|]{8,900}", RegexOptions.IgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(m.Value)) urlCandidates.Add(m.Value.Trim());
                }

                var authorId = Regex.Match(text, @"authorId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,32})", RegexOptions.IgnoreCase).Groups[1].Value;
                var liveStreamId = Regex.Match(text, @"liveStreamId[=:\""\\s]{0,6}([A-Za-z0-9_\-]{4,64})", RegexOptions.IgnoreCase).Groups[1].Value;

                foreach (var raw in urlCandidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
                {
                    var decoded = raw;
                    for (int i = 0; i < 2; i++)
                    {
                        var next = SafeUrlDecode(decoded);
                        if (string.Equals(next, decoded, StringComparison.Ordinal)) break;
                        decoded = next;
                    }

                    if (string.IsNullOrWhiteSpace(decoded)) continue;
                    if (!decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!ShouldLogKsDecodedUrl(decoded, 8)) continue;

                    var isUUrl = decoded.IndexOf("live.kuaishou.com/u/", StringComparison.OrdinalIgnoreCase) >= 0;
                    var isLiveUrl = decoded.IndexOf("alive.kuaishou.com/live/", StringComparison.OrdinalIgnoreCase) >= 0
                                 || decoded.IndexOf("live.kuaishou.com", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isUUrl && !isLiveUrl) continue;

                    Logger.LogInfo($"[KS_URL_DECODED] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} url={decoded}");
                    if (isUUrl)
                    {
                        Logger.LogInfo($"[KS_URL_U_HIT] channel={channel} file={fileBase} authorId={authorId} liveStreamId={liveStreamId} url={decoded}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_URL_DECODED] failed: {ex.Message}");
            }
        }

        private bool ShouldLogKsDecodedUrl(string url, int minSeconds)
        {
            var key = (url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key)) return false;

            var now = DateTime.Now;
            lock (ksFlowLock)
            {
                if (ksDecodedUrlLastLogAt.TryGetValue(key, out var lastAt) && (now - lastAt).TotalSeconds < minSeconds)
                {
                    return false;
                }
                ksDecodedUrlLastLogAt[key] = now;
            }
            return true;
        }

        private string SafeUrlDecode(string input)
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

        private string SanitizeFilePart(string input)
        {
            var s = string.IsNullOrWhiteSpace(input) ? "unknown" : input.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return cleaned.Length > 64 ? cleaned.Substring(0, 64) : cleaned;
        }

        //用于缓存接收过的消息ID，判断是否重复接收
        Dictionary<string, List<long>> msgDic = new Dictionary<string, List<long>>();

        //发送事件
        private void DoMessage(Message msg, string processName)
        {
            List<long> msgIdList;
            if (msgDic.ContainsKey(msg.Method))
            {
                msgIdList = msgDic[msg.Method];
            }
            else
            {
                msgIdList = new List<long>(320);
                msgDic.Add(msg.Method, msgIdList);
            }
            if (msgIdList.Contains(msg.msgId))
            {
                return;
            }

            msgIdList.Add(msg.msgId);
            //每种消息类型设置300容量应该足够,不太可能存在一条消息被挤出队列后再次出现
            while (msgIdList.Count > 300)
            {
                msgIdList.RemoveAt(0);
            }

            try
            {
                switch (msg.Method)
                {
                    //来了
                    case "WebcastMemberMessage":
                        {
                            var arg = Serializer.Deserialize<MemberMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnMemberMessage?.Invoke(this, new RoomMessageEventArgs<MemberMessage>(processName, arg));
                            break;
                        }
                    //关注
                    case "WebcastSocialMessage":
                        {
                            var arg = Serializer.Deserialize<SocialMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnSocialMessage?.Invoke(this, new RoomMessageEventArgs<SocialMessage>(processName, arg));
                            break;
                        }
                    //消息
                    case "WebcastChatMessage":
                        {
                            var arg = Serializer.Deserialize<ChatMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnChatMessage?.Invoke(this, new RoomMessageEventArgs<ChatMessage>(processName, arg));
                            break;
                        }
                    //点赞
                    case "WebcastLikeMessage":
                        {
                            var arg = Serializer.Deserialize<LikeMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnLikeMessage?.Invoke(this, new RoomMessageEventArgs<LikeMessage>(processName, arg));
                            break;
                        }
                    //礼物
                    case "WebcastGiftMessage":
                        {
                            var arg = Serializer.Deserialize<GiftMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnGiftMessage?.Invoke(this, new RoomMessageEventArgs<GiftMessage>(processName, arg));
                            break;
                        }
                    //直播间统计
                    case "WebcastRoomUserSeqMessage":
                        {
                            var arg = Serializer.Deserialize<RoomUserSeqMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnRoomUserSeqMessage?.Invoke(this, new RoomMessageEventArgs<RoomUserSeqMessage>(processName, arg));
                            break;
                        }
                    //直播间状态变更
                    case "WebcastControlMessage":
                        {
                            var arg = Serializer.Deserialize<ControlMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnControlMessage?.Invoke(this, new RoomMessageEventArgs<ControlMessage>(processName, arg));
                            break;
                        }
                    //粉丝团消息
                    case "WebcastFansclubMessage":
                        {
                            var arg = Serializer.Deserialize<FansclubMessage>(new ReadOnlyMemory<byte>(msg.Payload));
                            this.OnFansclubMessage?.Invoke(this, new RoomMessageEventArgs<FansclubMessage>(processName, arg));
                            break;
                        }
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                return;
            }
        }

        public class RoomMessageEventArgs<T> : EventArgs where T : class
        {
            /// <summary>
            /// 进程名
            /// </summary>
            public string Process { get; set; }

            /// <summary>
            /// 消息
            /// </summary>
            public T Message { get; set; }


            public RoomMessageEventArgs()
            {

            }

            public RoomMessageEventArgs(string process, T data)
            {
                this.Process = process;
                this.Message = data;
            }
        }
    }
}
