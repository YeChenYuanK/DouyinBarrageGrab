using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BarrageGrab.Kuaishou;
using BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Modles.ProtoEntity;
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
            if (!appsetting.ProcessFilter.Contains(e.ProcessName)) return;
            var buff = e.Payload;
            if (buff.Length == 0) return;

            // 判断是否为快手弹幕请求
            if (e.HostName != null && (e.HostName.Contains("kuaishou") || e.HostName.Contains("ksapis")))
            {
                ProcessKuaishouWsData(e);
                return;
            }

            //如果需要Gzip解压缩，但是开头字节不符合Gzip特征字节 则不处理
            if (e.NeedDecompress && buff[0] != 0x08) return;

            try
            {
                var enty = Serializer.Deserialize<WssResponse>(new ReadOnlyMemory<byte>(buff));
                if (enty == null) return;

                //检测包格式
                if (!enty.Headers.Any(a => a.Key == "compress_type" && a.Value == "gzip")) return;

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

            try
            {
                // 尝试 Protobuf 解析（快手直播伴侣可能用 Protobuf）
                ProcessKuaishouProtobuf(buff, e.ProcessName);
            }
            catch (Exception ex1)
            {
                // Protobuf 解析失败，尝试 JSON 解析
                try
                {
                    ProcessKuaishouJson(buff, e.ProcessName);
                }
                catch (Exception ex2)
                {
                    Logger.LogWarn($"[快手] WebSocket 数据解析失败: Protobuf({ex1.Message}), JSON({ex2.Message})");
                }
            }
        }

        // 处理快手 Protobuf 数据
        private void ProcessKuaishouProtobuf(byte[] buff, string processName)
        {
            var envelope = Serializer.Deserialize<Modles.ProtoEntity.KsSocketMessage>(new ReadOnlyMemory<byte>(buff));
            if (envelope?.Payload == null) return;

            var ksPayload = Serializer.Deserialize<Modles.ProtoEntity.KsPayload>(new ReadOnlyMemory<byte>(envelope.Payload));
            if (ksPayload?.SendMessages == null) return;

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
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.弹幕消息);
            AppRuntime.WsServer?.KsGrab_OnBarrage(null, new Kuaishou.KsBarragePusher.BarrageEventArgs(pack));
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
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.礼物消息);
            AppRuntime.WsServer?.KsGrab_OnBarrage(null, new Kuaishou.KsBarragePusher.BarrageEventArgs(pack));
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
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.点赞消息);
            AppRuntime.WsServer?.KsGrab_OnBarrage(null, new Kuaishou.KsBarragePusher.BarrageEventArgs(pack));
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
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.进直播间);
            AppRuntime.WsServer?.KsGrab_OnBarrage(null, new Kuaishou.KsBarragePusher.BarrageEventArgs(pack));
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
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.关注消息);
            AppRuntime.WsServer?.KsGrab_OnBarrage(null, new Kuaishou.KsBarragePusher.BarrageEventArgs(pack));
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
