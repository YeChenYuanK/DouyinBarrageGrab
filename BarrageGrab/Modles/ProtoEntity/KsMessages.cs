using System.Collections.Generic;
using ProtoBuf;

namespace BarrageGrab.Modles.ProtoEntity
{
    /// <summary>
    /// 快手 WebSocket 外层数据包
    /// payloadType 值：
    /// 200 = 认证请求
    /// 201 = 心跳
    /// 310 = 弹幕消息
    /// 320 = 礼物消息
    /// 330 = 点赞消息
    /// 340 = 统计消息
    /// 350 = 进入消息
    /// 360 = 关注消息
    /// </summary>
    [ProtoContract]
    public class KsSocketMessage
    {
        /// <summary>
        /// 压缩类型（0=无压缩）
        /// </summary>
        [ProtoMember(1)]
        public int CompressionType { get; set; }

        /// <summary>
        /// 消息类型（字符串或数字）
        /// </summary>
        [ProtoMember(2)]
        public string PayloadType { get; set; } = "";

        /// <summary>
        /// 消息载荷（二进制）
        /// </summary>
        [ProtoMember(3)]
        public byte[] Payload { get; set; }
    }

    [ProtoContract]
    public class KsPcSocketMessage
    {
        [ProtoMember(1)]
        public int PayloadType { get; set; }

        [ProtoMember(2)]
        public int CompressionType { get; set; }

        [ProtoMember(3)]
        public byte[] Payload { get; set; }
    }

    /// <summary>
    /// 快手认证请求（连接 WebSocket 后发送的第一个消息）
    /// </summary>
    [ProtoContract]
    public class KsAuthRequest
    {
        /// <summary>
        /// 平台标识（固定值 "LIVE_STREAM"）
        /// </summary>
        [ProtoMember(1)]
        public string Kpn { get; set; } = "";

        /// <summary>
        /// 平台类型（固定值 "WEB"）
        /// </summary>
        [ProtoMember(2)]
        public string Kpf { get; set; } = "";

        /// <summary>
        /// 直播间 ID
        /// </summary>
        [ProtoMember(3)]
        public string LiveStreamId { get; set; } = "";

        /// <summary>
        /// 认证令牌
        /// </summary>
        [ProtoMember(4)]
        public string Token { get; set; } = "";

        /// <summary>
        /// 页面 ID
        /// </summary>
        [ProtoMember(5)]
        public string PageId { get; set; } = "";
    }
    
    /// <summary>
    /// 快手心跳请求
    /// </summary>
    [ProtoContract]
    public class KsHeartbeatRequest
    {
        /// <summary>
        /// 当前时间戳（毫秒）
        /// </summary>
        [ProtoMember(1)]
        public long Timestamp { get; set; }
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
    /// 快手 PC 直播伴侣端特有的 Payload 结构
    /// 与网页版的 KsPayload 不同，它不是把类型作为字符串放在 SendMessage 里，
    /// 而是直接把不同类型的消息放在了不同的数字 Tag 数组中。
    /// </summary>
    [ProtoContract]
    public class KsPcPayload
    {
        // 假设 Tag 7 是聊天消息数组 (基于二进制分析)
        [ProtoMember(7, IsRequired = false)]
        public List<KsPcChatMessage> ChatMessages { get; } = new List<KsPcChatMessage>();
        
        // 假设 Tag 8 是礼物消息数组
        [ProtoMember(8, IsRequired = false)]
        public List<KsPcGiftMessage> GiftMessages { get; } = new List<KsPcGiftMessage>();
        
        // 假设 Tag 9 是点赞消息数组
        [ProtoMember(9, IsRequired = false)]
        public List<KsPcLikeMessage> LikeMessages { get; } = new List<KsPcLikeMessage>();

        // 假设 Tag 10 是进房消息数组
        [ProtoMember(10, IsRequired = false)]
        public List<KsPcEnterMessage> EnterMessages { get; } = new List<KsPcEnterMessage>();
    }

    [ProtoContract]
    public class KsPcChatMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        [ProtoMember(2)]
        public string Content { get; set; }
    }

    [ProtoContract]
    public class KsPcGiftMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        [ProtoMember(2)]
        public long GiftId { get; set; }

        [ProtoMember(3)]
        public string GiftName { get; set; }

        [ProtoMember(4)]
        public long Count { get; set; }
    }

    [ProtoContract]
    public class KsPcLikeMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }

        [ProtoMember(2)]
        public long Count { get; set; }
    }

    [ProtoContract]
    public class KsPcEnterMessage
    {
        [ProtoMember(1)]
        public KsUser User { get; set; }
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
