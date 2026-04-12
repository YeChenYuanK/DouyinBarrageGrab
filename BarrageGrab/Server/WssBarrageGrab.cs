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
            var buff = e.Payload;
            if (buff != null && buff.Length > 0)
            {
                DumpKuaishouRawBytes("ws_any_raw", $"{e.HostName}|{e.ProcessName}", buff);
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
            var isKuaishouHost = isLikelyKuaishouProcess || hostName.Contains("kuaishou") || hostName.Contains("wsukwai") || hostName.Contains("gifshow") || hostName.Contains("ksapis") || hostName.StartsWith("ksraw:") || hostName.StartsWith("ksrawtx:");
            // 快手官方客户端抓包场景允许按域名放行，但仍要求进程特征，避免浏览器/其他应用噪音误触发
            var allowKuaishouBypass = isKuaishouHost;
            if (!allowByProcessFilter && !allowKuaishouBypass)
            {
                return;
            }
            if (buff == null || buff.Length == 0)
            {
                return;
            }

            // 判断是否为快手弹幕请求
            if (isKuaishouHost)
            {
                ProcessKuaishouWsData(e);
                return;
            }

            //如果需要Gzip解压缩，但是开头字节不符合Gzip特征字节 则不处理
            if (e.NeedDecompress && buff[0] != 0x08)
            {
                return;
            }

            try
            {
                var enty = Serializer.Deserialize<WssResponse>(new ReadOnlyMemory<byte>(buff));
                if (enty == null)
                {
                    return;
                }

                //检测包格式
                if (!enty.Headers.Any(a => a.Key == "compress_type" && a.Value == "gzip"))
                {
                    return;
                }

                byte[] allBuff;
                //解压gzip
                allBuff = e.NeedDecompress ? Decompress(enty.Payload) : enty.Payload;
                var response = Serializer.Deserialize<Response>(new ReadOnlyMemory<byte>(allBuff));
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

            if (AppSetting.Current.KuaishouVerboseLog)
            {
                DumpKuaishouRawBytes("ws_raw", e.HostName, buff);
                Logger.LogInfo($"[快手] ProcessKuaishouWsData 收到数据 Len={buff.Length}");
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

            // 二层协议：外层二进制头 + 内层 GZIP（日志已确认存在 1F 8B 08）
            if (TryProcessKuaishouEmbeddedGzip(buff, e.ProcessName)) return;
            
            // 如果都没有命中，交到底层的 Protobuf 去解析（对于 103.* 直连 IP，由于前面可能没有 GZIP 或者已经是纯明文，走这里）
            ProcessKuaishouProtobuf(buff, e.ProcessName);
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

        private bool TryProcessKuaishouProtobufWithOffsets(byte[] buff, string processName)
        {
            foreach (var candidate in BuildKuaishouDecodeCandidates(buff))
            {
                try
                {
                    var span = new ReadOnlyMemory<byte>(candidate.Data, candidate.Offset, candidate.Data.Length - candidate.Offset);
                    byte[] innerPayload = null;
                    int compressionType = 0;

                    // 优先尝试 PC 端协议结构 (Field 1: PayloadType, Field 2: CompressionType, Field 3: Payload)
                    try
                    {
                        var pcEnvelope = Serializer.Deserialize<Modles.ProtoEntity.KsPcSocketMessage>(span);
                        if (pcEnvelope != null && pcEnvelope.Payload != null && pcEnvelope.Payload.Length > 0)
                        {
                            innerPayload = pcEnvelope.Payload;
                            compressionType = pcEnvelope.CompressionType;
                        }
                    }
                    catch
                    {
                        // 失败则尝试 Web 端协议结构 (Field 1: CompressionType, Field 2: PayloadType(String), Field 3: Payload)
                    }

                    if (innerPayload == null)
                    {
                        var envelope = Serializer.Deserialize<Modles.ProtoEntity.KsSocketMessage>(span);
                        if (envelope?.Payload == null || envelope.Payload.Length == 0) continue;
                        innerPayload = envelope.Payload;
                        compressionType = envelope.CompressionType;
                    }

                    // ====== 最强暴力容错：直接搜 GZIP 头，不依赖 Protobuf 字段 ======
                    int gzipOffset = -1;
                    for (int i = 0; i < innerPayload.Length - 1; i++)
                    {
                        if (innerPayload[i] == 0x1F && innerPayload[i+1] == 0x8B)
                        {
                            gzipOffset = i;
                            break;
                        }
                    }

                    // 如果第一层 payload 没有 GZIP，看看原始包里有没有 GZIP
                    if (gzipOffset == -1)
                    {
                        for (int i = 0; i < buff.Length - 1; i++)
                        {
                            if (buff[i] == 0x1F && buff[i+1] == 0x8B)
                            {
                                // 直接跨过所有的外层包壳，提取出 GZIP 数据！
                                byte[] toDecompress = new byte[buff.Length - i];
                                Buffer.BlockCopy(buff, i, toDecompress, 0, toDecompress.Length);
                                try
                                {
                                    byte[] decomp = Decompress(toDecompress);
                                    if (decomp != null && decomp.Length > 0)
                                    {
                                        innerPayload = decomp;
                                        gzipOffset = 0; // 标记已经解压成功
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }

                    if (compressionType == 2 || compressionType == 1 || gzipOffset != -1)
                    {
                        try
                        {
                            // 针对快手 PC 端的残缺 GZIP 进行容错处理
                            byte[] decompressed = null;
                            try
                            {
                                byte[] toDecompress = innerPayload;
                                if (gzipOffset > 0)
                                {
                                    toDecompress = new byte[innerPayload.Length - gzipOffset];
                                    Buffer.BlockCopy(innerPayload, gzipOffset, toDecompress, 0, toDecompress.Length);
                                }
                                decompressed = Decompress(toDecompress);
                            }
                            catch
                            {
                                // 如果自带的 Decompress 报错，可能没法处理结尾的错位数据，这里忽略错误
                            }
                            
                            if (decompressed != null && decompressed.Length > 0)
                            {
                                innerPayload = decompressed;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogInfo($"[快手] GZIP 解压失败: {ex.Message}");
                        }
                    }

                    // ====== 严格 Protobuf 解析 ======
                    
                    try 
                    {
                        var ksPcPayload = Serializer.Deserialize<Modles.ProtoEntity.KsPcPayload>(new ReadOnlyMemory<byte>(innerPayload));
                        
                        bool hasPcData = false;

                        if (ksPcPayload != null)
                        {
                            if (ksPcPayload.ChatMessages != null && ksPcPayload.ChatMessages.Count > 0)
                            {
                                hasPcData = true;
                                foreach(var chat in ksPcPayload.ChatMessages)
                                {
                                    FireKuaishouChat(new Modles.ProtoEntity.KsChatMessage
                                    {
                                        Content = chat.Content,
                                        User = chat.User
                                    });
                                }
                            }
                            
                            if (ksPcPayload.GiftMessages != null && ksPcPayload.GiftMessages.Count > 0)
                            {
                                hasPcData = true;
                                foreach(var gift in ksPcPayload.GiftMessages)
                                {
                                    FireKuaishouGift(new Modles.ProtoEntity.KsGiftMessage
                                    {
                                        User = gift.User,
                                        GiftId = gift.GiftId,
                                        GiftName = gift.GiftName,
                                        Count = gift.Count
                                    });
                                }
                            }

                            if (ksPcPayload.LikeMessages != null && ksPcPayload.LikeMessages.Count > 0)
                            {
                                hasPcData = true;
                                foreach(var like in ksPcPayload.LikeMessages)
                                {
                                    FireKuaishouLike(new Modles.ProtoEntity.KsLikeMessage
                                    {
                                        User = like.User,
                                        Count = like.Count
                                    });
                                }
                            }

                            if (ksPcPayload.EnterMessages != null && ksPcPayload.EnterMessages.Count > 0)
                            {
                                hasPcData = true;
                                foreach(var enter in ksPcPayload.EnterMessages)
                                {
                                    FireKuaishouEnter(new Modles.ProtoEntity.KsEnterMessage
                                    {
                                        User = enter.User
                                    });
                                }
                            }
                        }

                        if (hasPcData)
                        {
                            if (AppSetting.Current.KuaishouVerboseLog)
                            {
                                Logger.LogInfo($"[快手] PC 专属 Protobuf 解析成功！");
                            }
                            return true;
                        }
                    }
                    catch 
                    {
                        // 容错：如果解析成 KsPcPayload 失败，则继续尝试后续的 KsPayload
                    }

                    // 如果不是 PC 专属格式，尝试 Web 格式
                    try
                    {
                        var ksPayload = Serializer.Deserialize<Modles.ProtoEntity.KsPayload>(new ReadOnlyMemory<byte>(innerPayload));
                        
                        if (ksPayload?.SendMessages != null && ksPayload.SendMessages.Count > 0)
                        {
                            if (AppSetting.Current.KuaishouVerboseLog)
                            {
                                Logger.LogInfo($"[快手] Protobuf命中 strategy={candidate.Strategy} offset={candidate.Offset}，消息数={ksPayload.SendMessages.Count}");
                            }
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
                    }
                    catch
                    {
                        // 失败则忽略
                    }
                }
                catch
                {
                    // 继续尝试下一个候选
                }
            }
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
            foreach (var headerLen in new[] { 1, 2, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48 })
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

        // 处理快手 Protobuf 数据
        private void ProcessKuaishouProtobuf(byte[] buff, string processName)
        {
            if (TryProcessKuaishouProtobufWithOffsets(buff, processName))
            {
                return;
            }

            if (AppSetting.Current.KuaishouVerboseLog)
            {
                Logger.LogInfo($"[快手] Protobuf 所有候选解析失败。尝试保存样本...");
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
            // 如果用户信息为空，说明协议解析失败，直接返回不处理
            if (msg.User == null || string.IsNullOrEmpty(msg.User.Nickname))
            {
                Logger.LogWarn($"[快手][弹幕解析失败] 用户信息为空，Content: {msg.Content ?? ""}");
                return;
            }
            
            if (string.IsNullOrEmpty(msg.Content))
            {
                Logger.LogWarn($"[快手][弹幕解析失败] 内容为空，User: {msg.User.Nickname}");
                return;
            }

            Logger.LogInfo($"[快手][弹幕] {msg.User.Nickname}: {msg.Content}");
            var data = new JObject
            {
                ["Timestamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ["Content"] = msg.Content,
                ["User"] = new JObject
                {
                    ["Nickname"] = msg.User.Nickname,
                    ["HeadImgUrl"] = msg.User.HeadUrl ?? "",
                    ["SecUid"] = msg.User.UserId ?? ""
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

        // 移除 HTTP 和 Flow 相关的冗余探测与追踪代码

        private void DumpKuaishouRawBytes(string channel, string hostOrTag, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (!AppSetting.Current.KuaishouVerboseLog) return;
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "ks_raw");
                Directory.CreateDirectory(root);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                var fileBase = $"{ts}_{channel}_{data.Length}_{suffix}";
                var binPath = Path.Combine(root, fileBase + ".bin");
                File.WriteAllBytes(binPath, data);

                Logger.LogInfo($"[KS_RAW_DUMP] channel={channel} len={data.Length} file={fileBase}");
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[KS_RAW_DUMP] failed channel={channel}: {ex.Message}");
            }
        }

        //用于缓存接收过的消息ID，判断是否重复接收
        Dictionary<string, List<long>> msgDic = new Dictionary<string, List<long>>();
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

        private bool IsKsHeartbeatPacket(byte[] buff)
        {
            if (buff == null || buff.Length < 8) return false;
            
            // 基于真实样本分析：快手心跳包特征为27字节固定长度
            // 样本数据："CKwCEAEaCgjoBxCIJxignAEg7oHm9tcz" → 27字节解码后
            if (buff.Length == 27)
            {
                // 27字节是标准心跳包长度
                return true;
            }
            
            // 兼容其他常见心跳包长度范围
            if (buff.Length >= 25 && buff.Length <= 35)
            {
                // 常见心跳包长度范围
                return true;
            }
            
            // 特定字节模式检测（备用方案）
            if (buff.Length >= 16)
            {
                // 检查前几个字节的常见心跳模式
                if (buff[0] == 0x02 && buff[1] == 0x00 && buff[2] == 0x00)
                    return true;
                
                if (buff[0] == 0x03 && buff[1] == 0x00 && buff[4] == 0x00)
                    return true;
            }
            
            return false;
        }
    }
}
