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
            var isKuaishouHost = hostName.Contains("kuaishou") || hostName.Contains("wsukwai") || hostName.Contains("gifshow") || hostName.Contains("ksapis") || hostName.StartsWith("ksraw:");
            // 快手官方客户端抓包场景允许按域名放行，但仍要求进程特征，避免浏览器/其他应用噪音误触发
            var allowKuaishouBypass = isKuaishouHost && (isLikelyKuaishouProcess || hostName.StartsWith("ksraw:"));
            if (!allowByProcessFilter && !allowKuaishouBypass)
            {
                Logger.LogInfo($"[WS] 进程被过滤: {e.ProcessName}，不在白名单内");
                return;
            }
            var buff = e.Payload;
            if (buff.Length == 0)
            {
                Logger.LogInfo($"[WS] 空数据包，跳过");
                return;
            }

            Logger.LogInfo($"[WS] 收到数据 Host={e.HostName} Process={e.ProcessName} Len={buff.Length} NeedDecompress={e.NeedDecompress}");

            // 判断是否为快手弹幕请求
            if (isKuaishouHost)
            {
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

            Logger.LogInfo($"[快手] ProcessKuaishouWsData 收到数据 Len={buff.Length}");
            if (buff.Length >= 120 || (e.HostName ?? "").StartsWith("ksraw:", StringComparison.OrdinalIgnoreCase))
            {
                LogKuaishouPacketSignature(buff, e.HostName);
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
                    var inflated = Decompress(gzip);
                    if (inflated == null || inflated.Length == 0) continue;

                    Logger.LogInfo($"[快手] GZIP命中 at={i} inflatedLen={inflated.Length}");
                    DumpKsReverseSamples(buff, inflated, processName, i);
                    var wireSummary = BuildProtoWireSummary(inflated, maxFields: 24);
                    var isBroadcastCandidate = IsLikelyKuaishouBroadcastWire(wireSummary);
                    if (isBroadcastCandidate)
                    {
                        Logger.LogInfo($"[KS_REVERSE] classify=broadcast wire={wireSummary}");
                    }
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
                    if (!isActivityNoise && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(anchor)))
                    {
                        Logger.LogInfo($"[KS_ROOM_JSON] anchor={anchor ?? "N/A"}, title={title ?? "N/A"}");
                        // 恢复窗口提示：用于开播阶段快速观察，注意这里是 JSON 片段候选信息
                        Logger.PrintColor($"[快手房间候选] 主播: {anchor ?? "N/A"} | 标题: {title ?? "N/A"}", ConsoleColor.Cyan);
                    }

                    if (!isActivityNoise && IsKuaishouStateCallbackJobj(jobj))
                    {
                        foundStateCallback = true;
                        var stateTitle = title ?? jobj.SelectToken("$..liveTitle")?.Value<string>() ?? "N/A";
                        var stateAnchor = anchor ?? jobj.SelectToken("$..userName")?.Value<string>() ?? "N/A";
                        Logger.LogInfo($"[KS_STATE] anchor={stateAnchor}, title={stateTitle}");
                        Logger.PrintColor($"[快手状态] 主播: {stateAnchor} | 标题: {stateTitle}", ConsoleColor.DarkCyan);
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

            // 纯昵称词倾向降分，避免把“王翠花”误当评论
            if (IsLikelyKuaishouNickname(text)) score -= 50;

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
                "抢红包", "红包", "城市巅峰赛"
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

            var response = Serializer.Deserialize<Response>(new ReadOnlyMemory<byte>(payload));

            response.Messages.ForEach(f =>
            {
                DoMessage(f, e.ProcessName);
            });
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
