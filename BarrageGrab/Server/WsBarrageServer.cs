using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Fleck;
using BarrageGrab.Kuaishou;
using BarrageGrab.Modles;
using BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Modles.ProtoEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static BarrageGrab.Modles.ProtoEntity.Image;
using static BarrageGrab.Modles.WebCastGift;

namespace BarrageGrab
{
    /// <summary>
    /// WsBarrageServer 包装消息事件委托
    /// </summary>
    public delegate void PackMessageEventHandler(WsBarrageServer sender, WsBarrageServer.PackMsgEventArgs e);

    /// <summary>
    /// 弹幕服务
    /// </summary>
    public class WsBarrageServer : IDisposable
    {
        WebSocketServer socketServer; //Ws服务器对象
        Dictionary<string, UserState> socketList = new Dictionary<string, UserState>(); //客户端列表
        ConcurrentDictionary<string, Tuple<int, DateTime>> giftCountCache = new ConcurrentDictionary<string, Tuple<int, DateTime>>();//礼物计数缓存
        Timer dieout = new Timer(10000);//离线客户端清理计时器
        Timer giftCountTimer = new Timer(10000);//礼物缓存清理计时器
        WssBarrageGrab grab = new WssBarrageGrab();//弹幕解析器核心
        KsBarragePusher ksGrab; //快手弹幕抓取器（WebSocket直连）
        AppSetting Appsetting = AppSetting.Current;//全局配置文件实例
        static int printCount = 0; //控制台输出计数，用于判断清理控制台

        /// <summary>
        /// WS服务器启动地址
        /// </summary>
        public string ServerLocation => socketServer.Location;

        /// <summary>
        /// 数据内核
        /// </summary>
        public WssBarrageGrab Grab => grab;

        /// <summary>
        /// 快手弹幕内核（WebSocket直连模式）
        /// </summary>
        public KsBarragePusher KsGrab => ksGrab;

        /// <summary>
        /// 控制台打印事件
        /// </summary>
        public event EventHandler<PrintEventArgs> OnPrint;

        /// <summary>
        /// 是否已经释放资源
        /// </summary>
        public bool IsDisposed { get; private set; } = false;

        /// <summary>
        /// 服务关闭后触发
        /// </summary>
        public event EventHandler OnClose;

        /// <summary>
        /// 消息包装完成后触发
        /// </summary>
        public event PackMessageEventHandler OnPackMessage;

        public WsBarrageServer()
        {
            // 直接使用字符串构建 WebSocket URL，避免 IPAddress 对象转换问题
            var wsPort = Appsetting.WsProt;
            var listenHost = Appsetting.ListenAny ? "0.0.0.0" : "127.0.0.1";
            // 禁用双栈模式，避免 Windows 上的 IPv6/IPv4 双协议栈绑定问题
            var socket = new WebSocketServer($"ws://{listenHost}:{wsPort}", false);
            // 使用 OnConnect 事件模式替代直接赋值

            dieout.Elapsed += Dieout_Elapsed;
            giftCountTimer.Elapsed += GiftCountTimer_Elapsed;

            this.grab.OnChatMessage += Grab_OnChatMessage;
            this.grab.OnLikeMessage += Grab_OnLikeMessage;
            this.grab.OnMemberMessage += Grab_OnMemberMessage;
            this.grab.OnSocialMessage += Grab_OnSocialMessage;
            this.grab.OnSocialMessage += Grab_OnShardMessage;
            this.grab.OnGiftMessage += Grab_OnGiftMessage;
            this.grab.OnRoomUserSeqMessage += Grab_OnRoomUserSeqMessage;
            this.grab.OnFansclubMessage += Grab_OnFansclubMessage; ;
            this.grab.OnControlMessage += Grab_OnControlMessage;
            this.grab.OnKuaishouProxyBarrage += KsGrab_OnBarrage;

            // 初始化快手弹幕抓取器
            this.ksGrab = new KsBarragePusher();
            this.ksGrab.OnBarrage += KsGrab_OnBarrage;

            this.socketServer = socket;
            dieout.Start();
            giftCountTimer.Start();
        }

        /// <summary>
        /// 处理快手弹幕事件并广播
        /// </summary>
        private void KsGrab_OnBarrage(object sender, KsBarragePusher.BarrageEventArgs e)
        {
            var pack = e.Pack;
            var msgType = pack.Type;

            // 构造打印文本
            var json = pack.Data;
            try
            {
                var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<Msg>(json);
                if (msg != null)
                {
                    ConsoleColor color = AppSetting.Current.ColorMap[msgType].Item1;
                    var text = $"{DateTime.Now:HH:mm:ss} [快手直播] [{msgType}] {msg.Content}";

                    if (AppSetting.Current.BarrageLog)
                    {
                        Logger.LogBarrage(msgType, msg);
                    }

                    if (Appsetting.PrintBarrage &&
                        (!AppSetting.Current.PrintFilter.Any() || AppSetting.Current.PrintFilter.Contains(msgType.GetHashCode())))
                    {
                        OnPrint?.Invoke(this, new PrintEventArgs { Color = color, Message = text, MsgType = msgType });
                        Logger.PrintColor(text + "\n", color);
                    }
                }
            }
            catch { }

            // 推送给所有 WS 客户端
            Broadcast(pack);
        }

        //礼物缓存清理计时器回调
        private void GiftCountTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            var timeOutKeys = giftCountCache.Where(w => w.Value.Item2 < now.AddSeconds(-10) || w.Value == null).Select(s => s.Key).ToList();

            //淘汰过期的礼物计数缓存
            lock (giftCountCache)
            {
                timeOutKeys.ForEach(key =>
                {
                    Tuple<int, DateTime> _;
                    giftCountCache.TryRemove(key, out _);
                });
            }
        }

        //心跳淘汰计时器回调
        private void Dieout_Elapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            var dieoutKvs = socketList.Where(w => w.Value.LastPing.AddSeconds(dieout.Interval * 3) < now).ToList();
            dieoutKvs.ForEach(f => f.Value.Socket.Close());
        }

        //判断Rommid是否符合拦截规则
        private bool CheckRoomId(long roomid)
        {
            if (!AppSetting.Current.WebRoomIds.Any()) return true;

            var webrid = AppRuntime.RoomCaches.GetCachedWebRoomid(roomid.ToString());
            if (webrid.IsNullOrWhiteSpace()) return true;
            if (webrid == "未知") return true;

            return AppSetting.Current.WebRoomIds.Contains(webrid);
        }

        //解析用户
        private MsgUser GetUser(User data)
        {
            if (data == null) return null;
            MsgUser user = new MsgUser()
            {
                DisplayId = data.displayId,
                ShortId = data.shortId,
                Gender = data.Gender,
                Id = data.Id,
                Level = data.Level,
                PayLevel = (int)(data.payGrade?.Level ?? -1),
                Nickname = data.Nickname ?? "用户" + data.displayId,
                HeadImgUrl = data.avatarThumb?.urlLists?.FirstOrDefault() ?? "",
                SecUid = data.sec_uid,
                FollowerCount = data.followInfo?.followerCount ?? -1,
                FollowingCount = data.followInfo?.followingCount ?? -1,
                FollowStatus = data.followInfo?.followStatus ?? -1,
            };
            user.FansClub = new FansClubInfo()
            {
                ClubName = "",
                Level = 0
            };

            if (data.fansClub != null && data.fansClub.Data != null)
            {
                user.FansClub.ClubName = data.fansClub.Data.clubName;
                user.FansClub.Level = data.fansClub.Data.Level;
            }

            return user;
        }

        //检查属性定义
        private bool HasProperty(dynamic obj, string propertyName)
        {
            if (obj is ExpandoObject)
            {
                return ((IDictionary<string, object>)obj).ContainsKey(propertyName);
            }
            return obj.GetType().GetProperty(propertyName) != null;
        }

        //创建消息对象
        private T CreateMsg<T>(dynamic msg) where T : Msg, new()
        {
            var roomid = msg.Common.roomId.ToString();
            RoomInfo roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(roomid);

            //判断 Common 属性是否存在
            var hasUser = HasProperty(msg, nameof(msg.User));

            var enty = new T()
            {
                MsgId = msg.Common?.msgId,
                RoomId = roomid,
                WebRoomId = roomInfo?.WebRoomId ?? "",
                RoomTitle = roomInfo?.Title ?? "",
                IsAnonymous = roomInfo?.IsAnonymous ?? false,
                Appid = msg.Common.appId.ToString(),
                User = hasUser ? GetUser(msg.User) : null,
            };

            //判断是否是直播间管理员
            if (enty.User != null && roomInfo != null && roomInfo.AdminUserIds.Any())
            {
                enty.User.IsAdmin = roomInfo.AdminUserIds.Contains(enty.User.Id.ToString());
            }

            //判断是否是主播
            if (enty.User != null && roomInfo != null && roomInfo.Owner != null)
            {
                enty.User.IsAnchor = enty.User.Id.ToString() == roomInfo.Owner.UserId;
            }

            return enty;
        }

        //打印消息        
        private void PrintMsg(Msg msg, PackMsgType barType)
        {
            var rinfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(msg.RoomId.ToString());
            var roomName = (rinfo?.Owner?.Nickname ?? ((msg.WebRoomId.IsNullOrWhiteSpace() ? msg.RoomId.ToString() : msg.WebRoomId)));
            var isAdmin = msg.User != null && msg.User.IsAdmin;
            var isAnchor = msg.User != null && msg.User.IsAnchor;

            var text = $"{DateTime.Now.ToString("HH:mm:ss")} [{roomName}] [{barType}]";

            if (isAdmin)
            {
                text += " [管理员]";
            }

            if (isAnchor)
            {
                text += " [主播]";
            }

            if (msg.User != null)
            {
                text += $" [{msg.User?.GenderToString()}] ";
            }

            ConsoleColor color = AppSetting.Current.ColorMap[barType].Item1;
            var append = msg.Content;
            switch (barType)
            {
                case PackMsgType.弹幕消息: append = $"{msg?.User?.Nickname}: {msg.Content}"; break;
                case PackMsgType.下播: append = $"直播已结束"; break;
                default: break;
            }

            text += append;

            if (AppSetting.Current.BarrageLog)
            {
                Logger.LogBarrage(barType, msg);
            }

            if (!Appsetting.PrintBarrage) return;
            if (AppSetting.Current.PrintFilter.Any() && !AppSetting.Current.PrintFilter.Contains(barType.GetHashCode())) return;

            OnPrint?.Invoke(this, new PrintEventArgs()
            {
                Color = color,
                Message = text,
                MsgType = barType
            });

            if (++printCount > 10000)
            {
                Console.Clear();
                Logger.PrintColor("控制台已清理");
                printCount = 0;
            }
            Logger.PrintColor(text + "\n", color);
        }

        //发送消息包装事件
        private async void FirePack(Msg msg, PackMsgType barType)
        {
            if (OnPackMessage == null) return;
            var arg = new PackMsgEventArgs()
            {
                MsgType = barType,
                Message = msg
            };
            //异步执行
            OnPackMessage(this, arg);
        }

        //附加房间信息
        private void AttachRoomInfo(Msg msg)
        {
            if (msg == null) return;
            var roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(msg.RoomId.ToString());
            if (roomInfo == null) return;

            if (roomInfo.Owner != null)
            {
                msg.Owner = new RoomAnchorInfo()
                {
                    Nickname = roomInfo.Owner.Nickname,
                    HeadUrl = roomInfo.Owner.HeadUrl,
                    FollowStatus = roomInfo.Owner.FollowStatus,
                    SecUid = roomInfo.Owner.SecUid,
                    UserId = roomInfo.Owner.UserId
                };
            }
           

            if (msg.WebRoomId.IsNullOrWhiteSpace())
            {
                msg.WebRoomId = roomInfo.WebRoomId;
                msg.RoomTitle = roomInfo.Title;
                msg.IsAnonymous = roomInfo.IsAnonymous;
            }
        }

        //粉丝团
        private void Grab_OnFansclubMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<FansclubMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = CreateMsg<FansclubMsg>(msg);

            enty.Content = msg.Content;
            enty.Type = msg.Type;
            enty.Level = enty.User.FansClub.Level;

            var msgType = PackMsgType.粉丝团消息;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);
            Broadcast(new BarrageMsgPack(enty.ToJson(), msgType, e.Process));
        }

        //统计消息
        private void Grab_OnRoomUserSeqMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<RoomUserSeqMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = CreateMsg<UserSeqMsg>(msg);
            enty.OnlineUserCount = msg.Total;
            enty.TotalUserCount = msg.totalUser;
            enty.TotalUserCountStr = msg.totalPvForAnchor;
            enty.OnlineUserCountStr = msg.onlineUserForAnchor;
            enty.Content = $"当前直播间人数 {msg.onlineUserForAnchor}，累计直播间人数 {msg.totalPvForAnchor}";

            var msgType = PackMsgType.直播间统计;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //礼物
        private void Grab_OnGiftMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<GiftMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var key = msg.Common.roomId + "-" + msg.giftId + "-" + msg.groupId.ToString();

            int currCount = (int)msg.repeatCount;
            int lastCount = 0;

            //纠正赋值礼物数据，比如是否连击，弹幕回送会不准确
            //var findForData = giftData?.gifts?.FirstOrDefault(f => f.id == msg.giftId);
            //if (findForData != null)
            //{
            //    var ogift = msg.Gift;
            //    ogift.Name = findForData.name;
            //    ogift.Combo = findForData.combo;
            //    ogift.diamondCount = findForData.diamond_count;
            //    ogift.Name = findForData.name;
            //}

            //判断礼物重复
            if (msg.repeatEnd == 1 && giftCountCache.ContainsKey(key))
            {
                //清除缓存中的key
                if (msg.groupId > 0)
                {
                    Tuple<int, DateTime> _;
                    giftCountCache.TryRemove(key, out _);
                }
                return;
            }
            var backward = currCount <= lastCount;
            if (currCount <= 0) currCount = 1;

            if (giftCountCache.ContainsKey(key))
            {
                lastCount = giftCountCache[key].Item1;
                backward = currCount <= lastCount;
                if (!backward)
                {
                    lock (giftCountCache)
                    {
                        giftCountCache[key] = Tuple.Create(currCount, DateTime.Now);
                    }
                }
            }
            else
            {
                if (msg.groupId > 0 && !backward)
                {
                    giftCountCache.TryAdd(key, Tuple.Create(currCount, DateTime.Now));
                }
            }
            //比上次小，则说明先后顺序出了问题，直接丢掉，应为比它大的消息已经处理过了
            if (backward) return;

            var count = currCount - lastCount;

            var enty = CreateMsg<GiftMsg>(msg);
            enty.Content = $"{msg.User.Nickname} 送出 {msg.Gift.Name}{(msg.Gift.Combo ? "(可连击)" : "")} x {msg.repeatCount}个，增量{count}个";
            enty.DiamondCount = msg.Gift.diamondCount;
            enty.RepeatCount = msg.repeatCount;
            enty.GiftCount = count;
            enty.GroupId = msg.groupId;
            enty.GiftId = msg.giftId;
            enty.GiftName = msg.Gift.Name;
            enty.Combo = msg.Gift.Combo;
            enty.ImgUrl = msg.Gift.Image?.urlLists?.FirstOrDefault() ?? "";
            enty.ToUser = GetUser(msg.toUser);

            if (enty.ToUser != null)
            {
                enty.Content += "，给" + enty.ToUser.Nickname;
            }

            var msgType = PackMsgType.礼物消息;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), PackMsgType.礼物消息, e.Process);
            Broadcast(pack);
        }

        //关注
        private void Grab_OnSocialMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<SocialMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            if (msg.Action != 1) return;
            var enty = CreateMsg<Msg>(msg);
            enty.Content = $"{msg.User.Nickname} 关注了主播";

            var msgType = PackMsgType.关注消息;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //直播间分享
        private void Grab_OnShardMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<SocialMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            if (msg.Action != 3) return;
            ShareType type = ShareType.未知;
            if (Enum.IsDefined(type.GetType(), int.Parse(msg.shareTarget)))
            {
                type = (ShareType)int.Parse(msg.shareTarget);
            }

            var enty = CreateMsg<ShareMessage>(msg);
            enty.Content = $"{msg.User.Nickname} 分享了直播间到{type}";
            enty.ShareType = type;

            var msgType = PackMsgType.直播间分享;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);

            //shareTarget: (112:好友),(1微信)(2朋友圈)(3微博)(5:qq)(4:qq空间),shareType: 1            
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //来了
        private void Grab_OnMemberMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<Modles.ProtoEntity.MemberMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enterType = e.Message.userEnterTipType;
            var enty = CreateMsg<Modles.JsonEntity.MemberMessage>(msg);
            enty.Content = $"{msg.User.Nickname} ${(enterType == 6 ? " 通过分享" : "")}来了 直播间人数:{msg.memberCount}";
            enty.CurrentCount = msg.memberCount;
            enty.EnterTipType = enterType;

            var msgType = PackMsgType.进直播间;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //点赞
        private void Grab_OnLikeMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<LikeMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = CreateMsg<LikeMsg>(msg);
            enty.Total = msg.Total;
            enty.Count = msg.Count;
            enty.Content = $"{msg.User.Nickname} 为主播点了{msg.Count}个赞，总点赞{msg.Total}";

            var msgType = PackMsgType.点赞消息;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //弹幕
        private void Grab_OnChatMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<ChatMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = CreateMsg<Msg>(msg);
            enty.Content = msg.Content;

            var msgType = PackMsgType.弹幕消息;
            AttachRoomInfo(enty);
            FirePack(enty, msgType);
            PrintMsg(enty, msgType);

            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //直播间状态变更
        private void Grab_OnControlMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<ControlMessage> e)
        {
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            BarrageMsgPack pack = null;
            //下播
            if (msg.Status == 3)
            {
                var enty = new Msg()
                {
                    MsgId = msg.Common.msgId,
                    Content = "直播已结束",
                    RoomId = msg.Common.roomId.ToString(),
                    WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                    User = null,
                    Appid = e.Message.Common.appId.ToString()
                };

                var msgType = PackMsgType.下播;
                AttachRoomInfo(enty);
                FirePack(enty, msgType);
                PrintMsg(enty, msgType);
                pack = new BarrageMsgPack(enty.ToJson(), PackMsgType.下播, e.Process);
            }

            if (pack != null)
            {
                Broadcast(pack);
            }
        }

        //监听用户连接
        private void Listen(IWebSocketConnection socket)
        {
            //客户端url
            string clientUrl = socket.ConnectionInfo.ClientIpAddress + ":" + socket.ConnectionInfo.ClientPort;
            if (!socketList.ContainsKey(clientUrl))
            {
                socketList.Add(clientUrl, new UserState(socket));
                Logger.PrintColor($"{DateTime.Now.ToLongTimeString()} 已经建立与[{clientUrl}]的连接", ConsoleColor.Green);
            }
            else
            {
                socketList[clientUrl].Socket = socket;
            }

            //接收指令
            socket.OnMessage += (message) =>
            {
                try
                {
                    var cmdPack = JsonConvert.DeserializeObject<Command>(message);
                    if (cmdPack == null) return;
                }
                catch (Exception) { }
            };

            socket.OnClose += () =>
            {
                socketList.Remove(clientUrl);
                Logger.PrintColor($"{DateTime.Now.ToLongTimeString()} 已经关闭与[{clientUrl}]的连接", ConsoleColor.Red);
            };

            socket.OnPing += (data) =>
            {
                socketList[clientUrl].LastPing = DateTime.Now;
                socket.SendPong(Encoding.UTF8.GetBytes("pong"));
            };
        }

        /// <summary>
        /// 广播消息
        /// </summary>
        /// <param name="msg"></param>
        public void Broadcast(BarrageMsgPack pack)
        {
            if (pack == null) return;
            if (AppSetting.Current.PushFilter.Any() && !AppSetting.Current.PushFilter.Contains(pack.Type.GetHashCode())) return;

            var offLines = new List<string>();
            foreach (var item in socketList)
            {
                var state = item.Value;
                if (item.Value.Socket.IsAvailable)
                {
                    // 使用原始 JSON 格式（抖音嵌套格式，快手扁平格式）
                    state.Socket.Send(pack.ToJson());
                }
                else
                {
                    offLines.Add(item.Key);
                }
            }
            //删除掉线的套接字
            offLines.ForEach(key => socketList.Remove(key));
        }

        /// <summary>
        /// 开始监听
        /// </summary>
        public void StartListen()
        {
            try
            {
                this.grab.Start(); //启动代理
                this.socketServer.Start(Listen);//启动监听

                // 如果配置了快手弹幕抓取，则自动连接
                Logger.LogInfo($"[KS_CONFIG] KuaishouEnabled={AppSetting.Current.KuaishouEnabled}, KuaishouRoomId={AppSetting.Current.KuaishouRoomId}");
                if (AppSetting.Current.KuaishouEnabled && !string.IsNullOrWhiteSpace(AppSetting.Current.KuaishouRoomId))
                {
                    // sysProxy=true 时，优先走“官方客户端鉴权 + 代理抓包”，不主动直连快手 WS
                    if (AppSetting.Current.UsedProxy)
                    {
                        Logger.LogInfo("[KS_MODE] 当前为代理抓包模式：仅抓取官方客户端流量，不启动直连KsBarrageGrab");
                    }
                    else
                    {
                        var roomId = AppSetting.Current.KuaishouRoomId;
                        if (!string.IsNullOrWhiteSpace(AppSetting.Current.KuaishouCookie))
                        {
                            KsApiHelper.SetCookie(AppSetting.Current.KuaishouCookie);
                        }
                        _ = ksGrab.ConnectAsync(roomId).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                Logger.LogError(t.Exception?.InnerException ?? t.Exception, $"[KS] ConnectAsync 异常: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                        });
                        Logger.PrintColor($"[启动] 快手弹幕抓取已启动，房间ID: {roomId}", ConsoleColor.Green);
                    }
                }
            }
            catch (Exception)
            {
                this.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 关闭服务器连接，并关闭系统代理
        /// </summary>
        public void Dispose()
        {
            socketList.Values.ToList().ForEach(f => f.Socket.Close());
            socketList.Clear();
            socketServer.Dispose();
            grab.Dispose();
            ksGrab?.Dispose();  // 释放快手弹幕抓取器
            this.IsDisposed = true;
            this.OnClose?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 客户端套接字上下文
        /// </summary>
        class UserState
        {
            /// <summary>
            /// 套接字
            /// </summary>
            public IWebSocketConnection Socket { get; set; }

            /// <summary>
            /// 上次发起心跳包时间
            /// </summary>
            public DateTime LastPing { get; set; } = DateTime.Now;

            public UserState()
            {

            }
            public UserState(IWebSocketConnection socket)
            {
                Socket = socket;
            }
        }

        /// <summary>
        /// Print事件参数
        /// </summary>
        public class PrintEventArgs : EventArgs
        {
            public string Message { get; set; }

            public ConsoleColor Color { get; set; }

            public PackMsgType MsgType { get; set; }
        }

        /// <summary>
        /// 消息包事件参数
        /// </summary>
        public class PackMsgEventArgs
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public PackMsgType MsgType { get; set; }

            /// <summary>
            /// 消息内容
            /// </summary>
            public Msg Message { get; set; }
        }
    }
}
