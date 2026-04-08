using System.Collections.Generic;
using ProtoBuf;

namespace BarrageGrab.Modles.ProtoEntity
{
    /// <summary>
    /// 快手 WebSocket 外层数据包
    /// </summary>
    [ProtoContract]
    public class KsSocketMessage
    {
        [ProtoMember(1)]
        public string PayloadType { get; set; } = "";

        [ProtoMember(2)]
        public byte[] Payload { get; set; }
    }

    /// <summary>
    /// 快手下行推送消息体
    /// </summary>
    [ProtoContract]
    public class KsPayload
    {
        [ProtoMember(1)]
        public List<KsSendMessage> SendMessages { get; } = new List<KsSendMessage>();

        [ProtoMember(2)]
        public long ServerTimestamp { get; set; }
    }

    /// <summary>
    /// 快手单条下行消息（消息信封）
    /// </summary>
    [ProtoContract]
    public class KsSendMessage
    {
        /// <summary>
        /// 消息类型：CHAT / GIFT / LIKE / ENTER / FOLLOW / STAT / END
        /// </summary>
        [ProtoMember(1)]
        public string MsgType { get; set; } = "";

        /// <summary>
        /// 消息 Payload（按 MsgType 再反序列化为对应实体）
        /// </summary>
        [ProtoMember(2)]
        public byte[] Payload { get; set; }

        /// <summary>
        /// 消息唯一 ID，用于去重
        /// </summary>
        [ProtoMember(3)]
        public string MessageId { get; set; } = "";
    }

    /// <summary>
    /// 快手弹幕聊天消息
    /// </summary>
    [ProtoContract]
    public class KsChatMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        [ProtoMember(2)]
        public string Content { get; set; } = "";

        [ProtoMember(3)]
        public long Color { get; set; }
    }

    /// <summary>
    /// 快手礼物消息
    /// </summary>
    [ProtoContract]
    public class KsGiftMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        [ProtoMember(2)]
        public long GiftId { get; set; }

        [ProtoMember(3)]
        public string GiftName { get; set; } = "";

        /// <summary>
        /// 本次送出数量
        /// </summary>
        [ProtoMember(4)]
        public long Count { get; set; }

        /// <summary>
        /// 连击数量
        /// </summary>
        [ProtoMember(5)]
        public long ComboCount { get; set; }

        /// <summary>
        /// 礼物价值（快币）
        /// </summary>
        [ProtoMember(6)]
        public long Value { get; set; }

        /// <summary>
        /// 礼物图片 URL
        /// </summary>
        [ProtoMember(7)]
        public string GiftPic { get; set; } = "";
    }

    /// <summary>
    /// 快手点赞消息
    /// </summary>
    [ProtoContract]
    public class KsLikeMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        /// <summary>
        /// 本次点赞数量
        /// </summary>
        [ProtoMember(2)]
        public long Count { get; set; }

        /// <summary>
        /// 累计点赞总数
        /// </summary>
        [ProtoMember(3)]
        public long TotalCount { get; set; }
    }

    /// <summary>
    /// 快手用户进入直播间消息
    /// </summary>
    [ProtoContract]
    public class KsEnterMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        /// <summary>
        /// 当前观看人数
        /// </summary>
        [ProtoMember(2)]
        public long WatchCount { get; set; }
    }

    /// <summary>
    /// 快手关注消息
    /// </summary>
    [ProtoContract]
    public class KsFollowMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }
    }

    /// <summary>
    /// 快手直播间统计消息
    /// </summary>
    [ProtoContract]
    public class KsStatMessage
    {
        [ProtoMember(1)]
        public long WatchingCount { get; set; }

        [ProtoMember(2)]
        public long LikeCount { get; set; }

        [ProtoMember(3)]
        public string WatchingText { get; set; } = "";
    }

    /// <summary>
    /// 快手直播结束消息
    /// </summary>
    [ProtoContract]
    public class KsLiveEndMessage
    {
        [ProtoMember(1)]
        public int Reason { get; set; }
    }

    /// <summary>
    /// 快手用户信息
    /// </summary>
    [ProtoContract]
    public class KsUser
    {
        [ProtoMember(1)]
        public string UserId { get; set; } = "";

        [ProtoMember(2)]
        public string Nickname { get; set; } = "";

        /// <summary>
        /// 性别：1男 2女 0未知
        /// </summary>
        [ProtoMember(3)]
        public int Gender { get; set; }

        [ProtoMember(4)]
        public string HeadUrl { get; set; } = "";

        [ProtoMember(5)]
        public int Level { get; set; }

        [ProtoMember(6)]
        public int PayLevel { get; set; }

        [ProtoMember(7)]
        public string BadgeUrl { get; set; } = "";

        [ProtoMember(8)]
        public string UserSign { get; set; } = "";

        [ProtoMember(9)]
        public KsFansClub FansClub { get; set; }
    }

    /// <summary>
    /// 快手粉丝团信息
    /// </summary>
    [ProtoContract]
    public class KsFansClub
    {
        [ProtoMember(1)]
        public string ClubName { get; set; } = "";

        [ProtoMember(2)]
        public int Level { get; set; }

        /// <summary>
        /// 0=未加入 1=已加入
        /// </summary>
        [ProtoMember(3)]
        public int Status { get; set; }
    }
}
