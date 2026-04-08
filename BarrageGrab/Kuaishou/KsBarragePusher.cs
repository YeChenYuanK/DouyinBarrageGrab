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
    /// 将快手弹幕事件转换为统一的 BarrageMsgPack 格式，
    /// Data 部分为 Unity 兼容的扁平 JSON（secOpenid/nickName/avatarUrl/content 等顶层字段），
    /// 复用现有的 WsBarrageServer 进行广播，无需修改 Unity 侧代码。
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
        // 所有消息都构造为扁平 JSON，直接兼容 Unity 的 LiveCommentArgs / LiveGiftArgs / LiveLikeArgs 结构

        private void Grab_OnChatMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsChatMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["secOpenid"] = msg.User?.UserId ?? "",
                ["nickName"]  = msg.User?.Nickname ?? "快手用户",
                ["avatarUrl"] = msg.User?.HeadUrl ?? "",
                ["content"]   = msg.Content ?? ""
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.弹幕消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnGiftMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsGiftMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["secOpenid"] = msg.User?.UserId ?? "",
                ["nickName"]  = msg.User?.Nickname ?? "快手用户",
                ["avatarUrl"] = msg.User?.HeadUrl ?? "",
                // Unity 的 LiveGiftArgs 使用 secGiftId（string）而非 GiftId（long）
                ["secGiftId"] = msg.GiftId.ToString(),
                ["giftNum"]   = msg.Count,
                // 额外字段（Unity 忽略，但便于调试和日志）
                ["giftName"]  = msg.GiftName ?? "",
                ["content"]   = $"{msg.User?.Nickname ?? "某用户"} 送出 {msg.GiftName} x {msg.Count}"
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.礼物消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnLikeMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsLikeMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["secOpenid"] = msg.User?.UserId ?? "",
                ["nickName"]  = msg.User?.Nickname ?? "快手用户",
                ["avatarUrl"] = msg.User?.HeadUrl ?? "",
                ["likeNum"]   = msg.Count,
                // 额外字段
                ["totalLike"] = msg.TotalCount,
                ["content"]   = $"{msg.User?.Nickname ?? "某用户"} 点赞 x {msg.Count}"
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.点赞消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnEnterMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsEnterMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["secOpenid"] = msg.User?.UserId ?? "",
                ["nickName"]  = msg.User?.Nickname ?? "快手用户",
                ["avatarUrl"] = msg.User?.HeadUrl ?? "",
                ["content"]   = $"{msg.User?.Nickname ?? "某用户"} 来了，当前观看 {msg.WatchCount}"
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.进直播间);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnFollowMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsFollowMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                ["secOpenid"] = msg.User?.UserId ?? "",
                ["nickName"]  = msg.User?.Nickname ?? "快手用户",
                ["avatarUrl"] = msg.User?.HeadUrl ?? "",
                ["content"]   = $"{msg.User?.Nickname ?? "某用户"} 关注了主播"
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.关注消息);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnStatMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsStatMessage> e)
        {
            var msg = e.Message;
            var data = new JObject
            {
                // 统计类消息：Unity 没有对应的事件类型，作为额外信息传递
                ["watchingText"] = msg.WatchingText ?? "",
                ["watchingCount"] = msg.WatchingCount,
                ["likeCount"]     = msg.LikeCount,
                ["content"]       = $"在线人数 {msg.WatchingText}，点赞 {msg.LikeCount}"
            };
            var pack = BarrageMsgPack.Kuaishou(data.ToString(Formatting.None), PackMsgType.直播间统计);
            OnBarrage?.Invoke(this, new BarrageEventArgs(pack));
        }

        private void Grab_OnLiveEndMessage(object sender, KsBarrageGrab.KsMessageEventArgs<KsLiveEndMessage> e)
        {
            var data = new JObject
            {
                ["content"] = "直播已结束"
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
