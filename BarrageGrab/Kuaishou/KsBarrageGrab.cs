using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BarrageGrab.Modles.ProtoEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;

namespace BarrageGrab.Kuaishou
{
    /// <summary>
    /// 快手直播弹幕抓取器（基于 WebSocket 直连，无需系统代理）
    /// 适配快手直播伴侣和网页直播的弹幕协议
    /// </summary>
    public class KsBarrageGrab : IDisposable
    {
        // ---- 事件 ----
        public event EventHandler<KsMessageEventArgs<KsChatMessage>> OnChatMessage;
        public event EventHandler<KsMessageEventArgs<KsGiftMessage>> OnGiftMessage;
        public event EventHandler<KsMessageEventArgs<KsLikeMessage>> OnLikeMessage;
        public event EventHandler<KsMessageEventArgs<KsEnterMessage>> OnEnterMessage;
        public event EventHandler<KsMessageEventArgs<KsFollowMessage>> OnFollowMessage;
        public event EventHandler<KsMessageEventArgs<KsStatMessage>> OnStatMessage;
        public event EventHandler<KsMessageEventArgs<KsLiveEndMessage>> OnLiveEndMessage;

        // ---- 快手 WS 消息类型常量 ----
        private const string MSG_CHAT    = "CHAT";
        private const string MSG_GIFT    = "GIFT";
        private const string MSG_LIKE    = "LIKE";
        private const string MSG_ENTER   = "ENTER";
        private const string MSG_FOLLOW  = "FOLLOW";
        private const string MSG_STAT    = "STAT";
        private const string MSG_END     = "END";

        // 快手 WS 消息类型常量（Protobuf payloadType）
        private const int MSG_TYPE_AUTH = 200;        // 认证帧
        private const int MSG_TYPE_HEARTBEAT = 201;   // 心跳帧
        private const int MSG_TYPE_CHAT = 310;        // 弹幕消息
        private const int MSG_TYPE_GIFT = 320;        // 礼物消息
        private const int MSG_TYPE_LIKE = 330;        // 点赞消息
        private const int MSG_TYPE_STAT = 340;        // 统计消息
        private const int MSG_TYPE_ENTER = 350;       // 进入消息
        private const int MSG_TYPE_FOLLOW = 360;      // 关注消息
        
        // 心跳载荷：快手 WS 协议使用 Protobuf 心跳（payloadType=201）
        private static readonly byte[] HEARTBEAT_PAYLOAD = Encoding.UTF8.GetBytes(@"{""type"":""HEARTBEAT""}");

        // ---- 私有字段 ----
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private Timer _heartbeatTimer;
        private KsRoomInfo _roomInfo;
        private readonly KsApiHelper _apiHelper = new KsApiHelper();

        // 用于去重的消息 ID 缓存（每类型最多保留 500 条）
        private readonly ConcurrentDictionary<string, bool> _msgIdCache
            = new ConcurrentDictionary<string, bool>();

        private bool _disposed = false;

        /// <summary>
        /// 当前连接的直播间信息
        /// </summary>
        public KsRoomInfo RoomInfo => _roomInfo;

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => _ws?.State == WebSocketState.Open;

        // ===================================================================
        //  公开方法
        // ===================================================================

        /// <summary>
        /// 连接到指定快手直播间（通过主播快手号/短ID）
        /// </summary>
        /// <param name="userId">快手主播 userId，即直播间 URL 中 /u/ 之后的部分</param>
        public async Task ConnectAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("userId 不能为空", nameof(userId));

            Logger.LogInfo($"[KS] 开始获取直播间信息, userId={userId}");
            _roomInfo = await _apiHelper.GetRoomInfoAsync(userId);

            if (_roomInfo == null || !_roomInfo.IsLive)
            {
                Logger.LogWarn($"[KS] 直播间 {userId} 未开播或无法获取信息");
                return;
            }

            Logger.LogInfo($"[KS] 直播间信息: 主播={_roomInfo.AuthorName}, " +
                           $"标题={_roomInfo.Title}, liveStreamId={_roomInfo.LiveStreamId}");

            await ConnectByRoomInfoAsync(_roomInfo);
        }

        /// <summary>
        /// 直接通过已知的 KsRoomInfo 连接
        /// </summary>
        public async Task ConnectByRoomInfoAsync(KsRoomInfo roomInfo)
        {
            if (roomInfo == null) throw new ArgumentNullException(nameof(roomInfo));
            _roomInfo = roomInfo;

            if (!roomInfo.WebSocketUrls.Any())
            {
                Logger.LogWarn("[KS] 没有可用的 WebSocket 地址");
                return;
            }

            // 尝试所有备用地址
            foreach (var wsUrl in roomInfo.WebSocketUrls)
            {
                try
                {
                    await DoConnectAsync(wsUrl);
                    if (IsConnected) return; // 连接成功，退出尝试
                }
                catch (Exception ex)
                {
                    Logger.LogWarn($"[KS] WS 连接失败 {wsUrl}: {ex.Message}");
                }
            }

            Logger.LogWarn("[KS] 所有 WebSocket 地址均连接失败");
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _cts?.Cancel();
            _heartbeatTimer?.Dispose();
            if (_ws?.State == WebSocketState.Open)
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(2000);
            }
            _ws?.Dispose();
            _ws = null;
            Logger.LogInfo("[KS] WebSocket 已断开");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _apiHelper?.Dispose();
        }

        // ===================================================================
        //  私有方法：WS 连接与消息循环
        // ===================================================================

        private async Task DoConnectAsync(string wsUrl)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Origin", "https://live.kuaishou.com");
            _ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/122.0.0.0 Safari/537.36");

            Logger.LogInfo($"[KS] 正在连接 WebSocket: {wsUrl}");
            await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
            Logger.LogInfo($"[KS] WebSocket 已连接 ✓");

            // 发送认证帧（部分快手 WS 服务器要求先发送 auth）
            await SendAuthAsync();

            // 启动心跳定时器
            StartHeartbeat();

            // 启动接收循环（后台）
            _ = Task.Run(ReceiveLoopAsync, _cts.Token);
        }

        /// <summary>
        /// 发送认证/入场消息（快手 WS 握手后需发送认证帧）
        /// 新版协议使用 Protobuf 格式：payloadType=200
        /// </summary>
        private async Task SendAuthAsync()
        {
            if (_roomInfo == null) return;
            
            try
            {
                // 构建认证请求（FirstWSData.proto）
                var authRequest = new KsAuthRequest
                {
                    Kpn = "LIVE_STREAM",
                    Kpf = "WEB",
                    LiveStreamId = _roomInfo.LiveStreamId,
                    Token = _roomInfo.Token ?? "",
                    PageId = GeneratePageId()
                };
                
                // 先尝试 Protobuf 格式（新版协议）
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        Serializer.Serialize(ms, authRequest);
                        var authPayload = ms.ToArray();
                        
                        var socketMsg = new Modles.ProtoEntity.KsSocketMessage
                        {
                            PayloadType = MSG_TYPE_AUTH.ToString(),
                            Payload = authPayload
                        };
                        
                        using (var msgStream = new MemoryStream())
                        {
                            Serializer.Serialize(msgStream, socketMsg);
                            var msgBytes = msgStream.ToArray();
                            await _ws.SendAsync(new ArraySegment<byte>(msgBytes), WebSocketMessageType.Binary, true, _cts.Token);
                            Logger.LogInfo($"[KS] 已发送 Protobuf AUTH 帧 (payloadType={MSG_TYPE_AUTH}, payloadLen={authPayload.Length})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarn($"[KS] Protobuf AUTH 失败，尝试 JSON 格式: {ex.Message}");
                    
                    // Fallback 到 JSON 格式（旧版协议）
                    var auth = new
                    {
                        type = "AUTH",
                        liveStreamId = _roomInfo.LiveStreamId,
                        token = _roomInfo.Token ?? "",
                        pageId = authRequest.PageId
                    };
                    var authJson = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(auth));
                    await _ws.SendAsync(new ArraySegment<byte>(authJson), WebSocketMessageType.Text, true, _cts.Token);
                    Logger.LogInfo("[KS] 已发送 JSON AUTH 帧（兼容旧版协议）");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[KS] 发送 AUTH 帧失败: " + ex.Message);
            }
        }
        
        private static string GeneratePageId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        /// <summary>
        /// 启动心跳定时器（快手需要每 20 秒发送一次心跳）
        /// 优先使用 Protobuf 格式，失败则使用 JSON 格式
        /// </summary>
        private void StartHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = new Timer(async _ =>
            {
                try
                {
                    if (_ws?.State == WebSocketState.Open)
                    {
                        // 优先尝试 Protobuf 格式心跳
                        try
                        {
                            var heartbeat = new KsHeartbeatRequest
                            {
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            };
                            
                            using (var ms = new MemoryStream())
                            {
                                Serializer.Serialize(ms, heartbeat);
                                var payload = ms.ToArray();
                                
                                var socketMsg = new Modles.ProtoEntity.KsSocketMessage
                                {
                                    CompressionType = 0,
                                    PayloadType = MSG_TYPE_HEARTBEAT.ToString(),
                                    Payload = payload
                                };
                                
                                using (var msgStream = new MemoryStream())
                                {
                                    Serializer.Serialize(msgStream, socketMsg);
                                    var msgBytes = msgStream.ToArray();
                                    await _ws.SendAsync(
                                        new ArraySegment<byte>(msgBytes),
                                        WebSocketMessageType.Binary, true, _cts.Token);
                                }
                            }
                        }
                        catch
                        {
                            // Fallback 到 JSON 格式
                            await _ws.SendAsync(
                                new ArraySegment<byte>(HEARTBEAT_PAYLOAD),
                                WebSocketMessageType.Text, true, _cts.Token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarn("[KS] 心跳发送失败: " + ex.Message);
                }
            }, null, KsApiHelper.KS_HEARTBEAT_INTERVAL_MS, KsApiHelper.KS_HEARTBEAT_INTERVAL_MS);
        }

        /// <summary>
        /// WebSocket 消息接收循环
        /// </summary>
        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var dataBuffer = new List<byte>();

            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.LogInfo("[KS] 服务端关闭了 WebSocket 连接");
                        break;
                    }

                    dataBuffer.AddRange(buffer.Take(result.Count));

                    if (result.EndOfMessage)
                    {
                        var payload = dataBuffer.ToArray();
                        dataBuffer.Clear();
                        try
                        {
                            ProcessMessage(payload, result.MessageType);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "[KS] 处理消息时出错: " + ex.Message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不报错
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[KS] WebSocket 接收循环异常: " + ex.Message);
            }
            finally
            {
                Logger.LogInfo("[KS] 消息接收循环已退出");
            }
        }

        // ===================================================================
        //  私有方法：消息解析调度
        // ===================================================================

        /// <summary>
        /// 处理接收到的 WebSocket 原始帧
        /// </summary>
        private void ProcessMessage(byte[] payload, WebSocketMessageType messageType)
        {
            if (payload == null || payload.Length == 0) return;

            // 快手 WS 同时支持 Text(JSON) 和 Binary(Protobuf) 两种帧
            if (messageType == WebSocketMessageType.Text)
            {
                ProcessJsonMessage(payload);
            }
            else
            {
                ProcessBinaryMessage(payload);
            }
        }

        /// <summary>
        /// 处理 JSON 格式的消息帧（快手网页直播多用此格式）
        /// </summary>
        private void ProcessJsonMessage(byte[] payload)
        {
            var json = Encoding.UTF8.GetString(payload);
            JObject jobj;
            try { jobj = JObject.Parse(json); }
            catch { return; }

            var type = jobj["type"]?.Value<string>()?.ToUpper() ?? "";

            // 心跳 ACK 不处理
            if (type == "HEARTBEAT_ACK" || type == "HEARTBEAT") return;

            // 单条推送消息（data 字段是一个对象）
            if (jobj["data"] is JObject singleData)
            {
                DispatchJsonMsg(type, singleData, json);
                return;
            }

            // 批量推送（sendMessages 数组）
            if (jobj["sendMessages"] is JArray sendMsgs)
            {
                foreach (var item in sendMsgs)
                {
                    var msgType = item["msgType"]?.Value<string>()?.ToUpper() ?? "";
                    var data = item["payload"] as JObject;
                    var msgId = item["messageId"]?.Value<string>() ?? "";
                    if (!IsNewMessage(msgId)) continue;
                    if (data != null) DispatchJsonMsg(msgType, data, item.ToString());
                }
            }
        }

        /// <summary>
        /// 分发 JSON 消息到对应事件
        /// </summary>
        private void DispatchJsonMsg(string msgType, JObject data, string rawJson)
        {
            var roomId = _roomInfo?.LiveStreamId ?? "";
            switch (msgType)
            {
                case MSG_CHAT:
                    {
                        var msg = ParseJsonUser<KsChatMessage>(data);
                        msg.Content = data["content"]?.Value<string>() ?? "";
                        msg.Color = data["color"]?.Value<long>() ?? 0;
                        OnChatMessage?.Invoke(this, new KsMessageEventArgs<KsChatMessage>(roomId, msg));
                        break;
                    }
                case MSG_GIFT:
                    {
                        var msg = ParseJsonUser<KsGiftMessage>(data);
                        msg.GiftId = data["giftId"]?.Value<long>() ?? 0;
                        msg.GiftName = data["giftName"]?.Value<string>() ?? "";
                        msg.Count = data["count"]?.Value<long>() ?? 1;
                        msg.ComboCount = data["comboCount"]?.Value<long>() ?? msg.Count;
                        msg.Value = data["value"]?.Value<long>() ?? 0;
                        msg.GiftPic = data["giftPic"]?.Value<string>() ?? "";
                        OnGiftMessage?.Invoke(this, new KsMessageEventArgs<KsGiftMessage>(roomId, msg));
                        break;
                    }
                case MSG_LIKE:
                    {
                        var msg = ParseJsonUser<KsLikeMessage>(data);
                        msg.Count = data["count"]?.Value<long>() ?? 1;
                        msg.TotalCount = data["totalCount"]?.Value<long>() ?? 0;
                        OnLikeMessage?.Invoke(this, new KsMessageEventArgs<KsLikeMessage>(roomId, msg));
                        break;
                    }
                case MSG_ENTER:
                    {
                        var msg = ParseJsonUser<KsEnterMessage>(data);
                        msg.WatchCount = data["watchCount"]?.Value<long>() ?? 0;
                        OnEnterMessage?.Invoke(this, new KsMessageEventArgs<KsEnterMessage>(roomId, msg));
                        break;
                    }
                case MSG_FOLLOW:
                    {
                        var msg = ParseJsonUser<KsFollowMessage>(data);
                        OnFollowMessage?.Invoke(this, new KsMessageEventArgs<KsFollowMessage>(roomId, msg));
                        break;
                    }
                case MSG_STAT:
                    {
                        var msg = new KsStatMessage
                        {
                            WatchingCount = data["watchingCount"]?.Value<long>() ?? 0,
                            LikeCount = data["likeCount"]?.Value<long>() ?? 0,
                            WatchingText = data["watchingText"]?.Value<string>() ?? ""
                        };
                        OnStatMessage?.Invoke(this, new KsMessageEventArgs<KsStatMessage>(roomId, msg));
                        break;
                    }
                case MSG_END:
                    {
                        var msg = new KsLiveEndMessage
                        {
                            Reason = data["reason"]?.Value<int>() ?? 0
                        };
                        OnLiveEndMessage?.Invoke(this, new KsMessageEventArgs<KsLiveEndMessage>(roomId, msg));
                        break;
                    }
                default:
                    // 未知类型，暂不处理
                    break;
            }
        }

        /// <summary>
        /// 处理 Binary(Protobuf) 格式的消息帧（快手直播伴侣/新版接口）
        /// </summary>
        private void ProcessBinaryMessage(byte[] payload)
        {
            // 快手 Protobuf 帧可能带有 zlib/gzip 压缩，先尝试解压
            byte[] data = TryDecompress(payload);

            try
            {
                // 外层信封
                var envelope = Serializer.Deserialize<KsSocketMessage>(new ReadOnlyMemory<byte>(data));
                if (envelope?.Payload == null) return;

                // 内层 KsPayload（包含 sendMessages 列表）
                var ksPayload = Serializer.Deserialize<KsPayload>(new ReadOnlyMemory<byte>(envelope.Payload));
                if (ksPayload?.SendMessages == null) return;

                foreach (var sendMsg in ksPayload.SendMessages)
                {
                    if (!IsNewMessage(sendMsg.MessageId)) continue;
                    DispatchProtoMsg(sendMsg);
                }
            }
            catch (Exception ex)
            {
                // Protobuf 帧可能不是标准结构，降级尝试 JSON 解析
                try { ProcessJsonMessage(data); }
                catch { Logger.LogWarn("[KS] Binary 消息解析失败: " + ex.Message); }
            }
        }

        /// <summary>
        /// 分发 Protobuf 消息到对应事件
        /// </summary>
        private void DispatchProtoMsg(KsSendMessage sendMsg)
        {
            var roomId = _roomInfo?.LiveStreamId ?? "";
            var type = (sendMsg.MsgType ?? "").ToUpper();
            var payload = sendMsg.Payload;
            if (payload == null) return;

            try
            {
                switch (type)
                {
                    case MSG_CHAT:
                        {
                            var msg = Serializer.Deserialize<KsChatMessage>(new ReadOnlyMemory<byte>(payload));
                            OnChatMessage?.Invoke(this, new KsMessageEventArgs<KsChatMessage>(roomId, msg));
                            break;
                        }
                    case MSG_GIFT:
                        {
                            var msg = Serializer.Deserialize<KsGiftMessage>(new ReadOnlyMemory<byte>(payload));
                            OnGiftMessage?.Invoke(this, new KsMessageEventArgs<KsGiftMessage>(roomId, msg));
                            break;
                        }
                    case MSG_LIKE:
                        {
                            var msg = Serializer.Deserialize<KsLikeMessage>(new ReadOnlyMemory<byte>(payload));
                            OnLikeMessage?.Invoke(this, new KsMessageEventArgs<KsLikeMessage>(roomId, msg));
                            break;
                        }
                    case MSG_ENTER:
                        {
                            var msg = Serializer.Deserialize<KsEnterMessage>(new ReadOnlyMemory<byte>(payload));
                            OnEnterMessage?.Invoke(this, new KsMessageEventArgs<KsEnterMessage>(roomId, msg));
                            break;
                        }
                    case MSG_FOLLOW:
                        {
                            var msg = Serializer.Deserialize<KsFollowMessage>(new ReadOnlyMemory<byte>(payload));
                            OnFollowMessage?.Invoke(this, new KsMessageEventArgs<KsFollowMessage>(roomId, msg));
                            break;
                        }
                    case MSG_STAT:
                        {
                            var msg = Serializer.Deserialize<KsStatMessage>(new ReadOnlyMemory<byte>(payload));
                            OnStatMessage?.Invoke(this, new KsMessageEventArgs<KsStatMessage>(roomId, msg));
                            break;
                        }
                    case MSG_END:
                        {
                            var msg = Serializer.Deserialize<KsLiveEndMessage>(new ReadOnlyMemory<byte>(payload));
                            OnLiveEndMessage?.Invoke(this, new KsMessageEventArgs<KsLiveEndMessage>(roomId, msg));
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarn($"[KS] Protobuf 消息 {type} 解析失败: {ex.Message}");
            }
        }

        // ===================================================================
        //  辅助方法
        // ===================================================================

        /// <summary>
        /// 从 JSON data 节点中解析用户信息并填入实体
        /// </summary>
        private static T ParseJsonUser<T>(JObject data) where T : new()
        {
            var instance = new T();
            var authorProp = typeof(T).GetProperty("User");
            if (authorProp == null) return instance;

            var authorNode = data["user"] ?? data["author"];
            if (authorNode == null) return instance;

            var user = new KsUser
            {
                UserId = authorNode["userId"]?.Value<string>() ??
                         authorNode["id"]?.Value<string>() ?? "",
                Nickname = authorNode["name"]?.Value<string>() ??
                           authorNode["nickname"]?.Value<string>() ?? "",
                HeadUrl = authorNode["headUrl"]?.Value<string>() ??
                          authorNode["avatar"]?.Value<string>() ?? "",
                Gender = authorNode["gender"]?.Value<int>() ?? 0,
                Level = authorNode["level"]?.Value<int>() ?? 0,
                PayLevel = authorNode["payLevel"]?.Value<int>() ?? 0
            };

            var fansNode = authorNode["fansClub"] ?? authorNode["fans_club"];
            if (fansNode != null)
            {
                user.FansClub = new KsFansClub
                {
                    ClubName = fansNode["clubName"]?.Value<string>() ?? "",
                    Level = fansNode["level"]?.Value<int>() ?? 0,
                    Status = fansNode["status"]?.Value<int>() ?? 0
                };
            }

            authorProp.SetValue(instance, user);
            return instance;
        }

        /// <summary>
        /// 尝试对数据进行 gzip/zlib 解压，失败则返回原始数据
        /// </summary>
        private static byte[] TryDecompress(byte[] data)
        {
            if (data == null || data.Length < 2) return data;

            // gzip 魔数：1f 8b
            if (data[0] == 0x1f && data[1] == 0x8b)
            {
                try
                {
                    using (var ms = new MemoryStream(data))
                    using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                    using (var out_ = new MemoryStream())
                    {
                        gz.CopyTo(out_);
                        return out_.ToArray();
                    }
                }
                catch { }
            }

            // zlib 魔数：78 9c / 78 01 / 78 da
            if (data[0] == 0x78)
            {
                try
                {
                    using (var ms = new MemoryStream(data, 2, data.Length - 2)) // 跳过 zlib 2 字节头
                    using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
                    using (var out_ = new MemoryStream())
                    {
                        deflate.CopyTo(out_);
                        return out_.ToArray();
                    }
                }
                catch { }
            }

            return data;
        }

        /// <summary>
        /// 判断消息是否为新消息（用于去重）
        /// </summary>
        private bool IsNewMessage(string msgId)
        {
            if (string.IsNullOrWhiteSpace(msgId)) return true;
            if (_msgIdCache.ContainsKey(msgId)) return false;
            _msgIdCache[msgId] = true;
            // 超过 2000 条时清理旧缓存
            if (_msgIdCache.Count > 2000)
            {
                var toRemove = _msgIdCache.Keys.Take(500).ToList();
                foreach (var k in toRemove) _msgIdCache.TryRemove(k, out _);
            }
            return true;
        }

        // ===================================================================
        //  内嵌类型
        // ===================================================================

        /// <summary>
        /// 快手消息事件参数
        /// </summary>
        public class KsMessageEventArgs<T> : EventArgs where T : class
        {
            /// <summary>
            /// 直播间 liveStreamId
            /// </summary>
            public string LiveStreamId { get; }

            /// <summary>
            /// 消息内容
            /// </summary>
            public T Message { get; }

            public KsMessageEventArgs(string liveStreamId, T message)
            {
                LiveStreamId = liveStreamId;
                Message = message;
            }
        }
    }
}
