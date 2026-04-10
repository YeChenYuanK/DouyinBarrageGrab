using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
                    LogKuaishouPacketSignature(inflated, $"ksgzip:{i}");
                    TryLogKuaishouSessionInfo(inflated);

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
                    if (TryProcessKuaishouGenericProtoText(inflated))
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

        private readonly List<string> _ksSessionDedup = new List<string>();
        private readonly object _ksSessionDedupLock = new object();
        private void TryLogKuaishouSessionInfo(byte[] inflated)
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
                TryEmitFallbackChatFromHints(zh);
            }
            catch
            {
                // ignore
            }
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

        private bool IsLikelyKuaishouChatText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 2 || text.Length > 50) return false;
            if (text.Contains("http") || text.Contains("kwailive://") || text.Contains(".png") || text.Contains(".webp")) return false;
            if (Regex.IsMatch(text, @"^[0-9a-fA-F\-]{16,}$")) return false; // guid/hash
            var blacklist = new[]
            {
                "livePeakCup", "MERCHANT_", "lottie", "stickerImage", "正在看", "直播间正在开启", "host-name", "result",
                "快手平台账号", "未成年人", "严禁主播", "人气里程碑", "欢迎开播"
            };
            if (blacklist.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return false;

            var cjkCount = text.Count(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            var letterOrDigit = text.Count(char.IsLetterOrDigit);
            return cjkCount >= 2 || (letterOrDigit >= 4 && text.Any(ch => ch == ' '));
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
            Logger.LogInfo($"[快手] 触发弹幕事件: {msg.User?.Nickname}: {msg.Content}");
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
