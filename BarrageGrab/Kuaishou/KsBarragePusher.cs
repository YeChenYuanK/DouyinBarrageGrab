using System;
using System.Collections.Generic;
using BarrageGrab.Modles;
using BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Modles.ProtoEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BarrageGrab.Kuaishou
{
    /// <summary>
    /// 快手弹幕推送器
    /// 将快手弹幕事件转换为抖音的 Msg 嵌套格式，
    /// 游戏只需接一套协议，无需为每个平台单独适配。
    /// </summary>
    public class KsBarragePusher : IDisposable
    {
        private KsBarrageGrab _grab;

        public KsBarragePusher()
        {
        }

        /// <summary>
        /// 连接到指定的快手直播间
        /// </summary>
        public async System.Threading.Tasks.Task ConnectAsync(string userId)
        {
            if (_grab != null)
            {
                _grab.OnChatMessage -= Grab_OnChatMessage;
                _grab.OnGiftMessage -= Grab_OnGiftMessage;
                _grab.OnLikeMessage -= Grab_OnLikeMessage;
                _grab.OnEnterMessage -= Grab_OnEnterMessage;
                _grab.OnFollowMessage -= Grab_OnFollowMessage;
                _grab.OnStatMessage -= Grab_OnStatMessage;
                _grab.OnLiveEndMessage -= Grab_OnLiveEndMessage;
                _grab.Dispose();
            }

            _grab = new KsBarrageGrab();

            _grab.OnChatMessage += Grab_OnChatMessage;
            _grab.OnGiftMessage += Grab_OnGiftMessage;
            _grab.OnLikeMessage += Grab_OnLikeMessage;
            _grab.OnEnterMessage += Grab_OnEnterMessage;
            _grab.OnFollowMessage += Grab_OnFollowMessage;
            _grab.OnStatMessage += Grab_OnStatMessage;
            _grab.OnLiveEndMessage += Grab_OnLiveEndMessage;

            await _grab.ConnectAsync(userId);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _grab?.Disconnect();
        }

        public void Dispose()
        {
            _grab?.Dispose();
        }

        // ==================== 事件处理 ====================
        // 所有消息都构造为抖音的 Msg 嵌套格式（User 字段内嵌），
        // 与抖音弹幕格式保持一致，游戏只需接一套协议。

        /// <summary>
        /// 将快手用户转换为抖音 MsgUser 嵌套格式
        /// </summary>
        private static JObject BuildMsgUser(KsUser ksUser)
        {
            if (ksUser == null)
                return new JObject();

            return new JObject
            {
                ["Nickname"] = ksUser.Nickname ?? "快手用户",
                ["HeadImgUrl"] = ksUser.HeadUrl ?? "",
                ["SecUid"] = ksUser.UserId ?? "",
                ["Gender"] = ksUser.Gender,
                ["Level"] = ksUser.Level,
                ["PayLevel"] = ksUser.PayLevel
            };
        }

        private void Grab_OnChatMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsChatMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["Content"] = msg.Content ?? "",
                ["User"] = BuildMsgUser(msg.User)
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.弹幕消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnGiftMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsGiftMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 送出 {msg.GiftName} x {msg.Count}",
                ["User"] = BuildMsgUser(msg.User),
                ["GiftId"] = msg.GiftId,
                ["GiftName"] = msg.GiftName ?? "",
                ["Count"] = msg.Count
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.礼物消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnLikeMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsLikeMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 点赞 x {msg.Count}",
                ["User"] = BuildMsgUser(msg.User),
                ["Count"] = msg.Count,
                ["TotalCount"] = msg.TotalCount
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.点赞消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnEnterMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsEnterMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 来了",
                ["User"] = BuildMsgUser(msg.User),
                ["WatchCount"] = msg.WatchCount
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.进直播间);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnFollowMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsFollowMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["Content"] = $"{msg.User?.Nickname ?? "某用户"} 关注了主播",
                ["User"] = BuildMsgUser(msg.User)
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.关注消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnStatMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsStatMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["Content"] = $"在线人数 {msg.WatchingText}，点赞 {msg.LikeCount}",
                ["WatchingText"] = msg.WatchingText ?? "",
                ["WatchingCount"] = msg.WatchingCount,
                ["LikeCount"] = msg.LikeCount
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.直播间统计);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnLiveEndMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsLiveEndMessage> e)
        {
            var data = new JObject
            {
                ["Content"] = "直播已结束"
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.下播);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        // ==================== 公开事件 ====================

        /// <summary>
        /// 弹幕消息事件（推送给 WsBarrageServer 用）
        /// </summary>
        public event EventHandler<BarrageEventArgs> OnBarrage;

        /// <summary>
        /// 事件参数：包含可直接广播的 BarrageMsgPack
        /// </summary>
        public class BarrageEventArgs : EventArgs
        {
            public BarrageMsgPack Pack { get; }
            public BarrageEventArgs(BarrageMsgPack pack)
            {
                Pack = pack;
            }
        }
    }
}
