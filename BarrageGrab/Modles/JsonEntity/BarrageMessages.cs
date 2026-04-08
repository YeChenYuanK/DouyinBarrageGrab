using System;
using System.ComponentModel;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Org.BouncyCastle.Asn1.Crmf;

namespace BarrageGrab.Modles.JsonEntity
{
    /// <summary>
    /// 弹幕消息类型
    /// </summary>
    public enum PackMsgType
    {
        [Description("无")]
        无 = 0,
        [Description("消息")]
        弹幕消息 = 1,
        [Description("点赞")]
        点赞消息 = 2,
        [Description("进房")]
        进直播间 = 3,
        [Description("关注")]
        关注消息 = 4,
        [Description("礼物")]
        礼物消息 = 5,
        [Description("统计")]
        直播间统计 = 6,
        [Description("粉团")]
        粉丝团消息 = 7,
        [Description("分享")]
        直播间分享 = 8,
        [Description("下播")]
        下播 = 9
    }

    /// <summary>
    /// 直播平台来源
    /// </summary>
    public enum Platform
    {
        Unknown = 0,
        Douyin = 1,    // 抖音
        Kuaishou = 2   // 快手
    }

    /// <summary>
    /// 粉丝团消息类型
    /// </summary>
    public enum FansclubType
    {
        无 = 0,
        粉丝团升级 = 1,
        加入粉丝团 = 2
    }

    /// <summary>
    /// 直播间分享目标
    /// </summary>
    public enum ShareType
    {
        未知 = 0,
        微信 = 1,
        朋友圈 = 2,
        微博 = 3,
        QQ空间 = 4,
        QQ = 5,
        抖音好友 = 112
    }

    /// <summary>
    /// 观众的进入方式
    /// </summary>
    public enum EnterType
    {
        正常进入 = 0,
        通过分享进入 = 6,
        //...其他暂时未知
    }

    /// <summary>
    /// 数据包装器
    /// </summary>
    public class BarrageMsgPack
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        public PackMsgType Type { get; set; }

        /// <summary>
        /// 进程名
        /// </summary>
        public string ProcessName { get; set; }

        /// <summary>
        /// 消息对象
        /// </summary>
        public string Data { get; set; }

        /// <summary>
        /// 平台来源（用于区分抖音/快手）
        /// </summary>
        public Platform Platform { get; set; } = Platform.Douyin;

        public BarrageMsgPack()
        {

        }

        public BarrageMsgPack(string data, PackMsgType type, string processName, Platform platform = Platform.Douyin)
        {
            Data = data;
            Type = type;
            ProcessName = processName;
            Platform = platform;
        }

        /// <summary>
        /// 构造快手平台的 BarrageMsgPack
        /// </summary>
        public static BarrageMsgPack Kuaishou(string data, PackMsgType type, string processName = "快手弹幕")
        {
            return new BarrageMsgPack(data, type, processName, Platform.Kuaishou);
        }

        /// <summary>
        /// 生成 Unity 兼容的扁平化 JSON
        /// 将嵌套的 User 对象展开为顶层字段，兼容野套圈等 Unity 项目的解析逻辑
        /// 
        /// Unity 期望的 Data 格式（以评论为例）：
        ///   { "secOpenid":"xxx", "nickName":"xxx", "avatarUrl":"xxx", "content":"xxx" }
        /// 
        /// 内部 Douyin 格式：
        ///   { "Content":"xxx", "User":{"Nickname":"xxx","HeadImgUrl":"xxx","SecUid":"xxx"} }
        /// </summary>
        public string ToUnityJson()
        {
            // 快手平台：Data 已经是扁平格式，直接输出
            if (this.Platform == Platform.Kuaishou)
            {
                return $"{{\"Type\":{(int)this.Type},\"Data\":{this.Data}}}";
            }

            // 抖音平台：将嵌套的 User 对象扁平化
            return FlattenDouyinData(this);
        }

        private static string FlattenDouyinData(BarrageMsgPack pack)
        {
            if (string.IsNullOrEmpty(pack.Data))
                return pack.ToJson();

            try
            {
                var dataObj = Newtonsoft.Json.Linq.JObject.Parse(pack.Data);
                var flatData = new Newtonsoft.Json.Linq.JObject();

                // 遍历所有字段
                foreach (var prop in dataObj.Properties())
                {
                    string key = prop.Name;

                    // User 字段：展开为顶层字段（兼容 Unity 期望的 secOpenid/nickName/avatarUrl）
                    if (key == "User" && prop.Value.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var userObj = (Newtonsoft.Json.Linq.JObject)prop.Value;
                        // 映射：SecUid -> secOpenid（Unity 期望的字段名）
                        if (userObj.TryGetValue("SecUid", out var secUid))
                            flatData["secOpenid"] = secUid;
                        if (userObj.TryGetValue("Nickname", out var nickname))
                            flatData["nickName"] = nickname;
                        if (userObj.TryGetValue("HeadImgUrl", out var headImg))
                            flatData["avatarUrl"] = headImg;
                        // GiftId 是 long，Unity 期望 string 类型的 secGiftId
                        // 检查 User 下是否有 GiftId（某些消息类型）
                        if (userObj.TryGetValue("GiftId", out var giftId))
                            flatData["secGiftId"] = giftId?.ToString() ?? "";
                    }
                    else if (key == "GiftId")
                    {
                        flatData["secGiftId"] = prop.Value?.ToString() ?? "";
                    }
                    else
                    {
                        flatData[key] = prop.Value;
                    }
                }

                // 构造 Unity 期望的最终格式（Type + Data 顶层，兼容现有 Unity 代码）
                var unityData = new Newtonsoft.Json.Linq.JObject
                {
                    ["Type"] = (int)pack.Type,
                    ["Data"] = flatData
                };
                return unityData.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                // 解析失败，降级为原始格式
                return pack.ToJson();
            }
        }

        /// <summary>
        /// 当收到弹幕消息时执行回调
        /// </summary>
        public void IfChatMsg(Action<Msg> action) => IfTypedMsg(PackMsgType.弹幕消息, action);

        /// <summary>
        /// 当收到点赞消息时执行回调
        /// </summary>
        public void IfLikeMsg(Action<LikeMsg> action) => IfTypedMsg(PackMsgType.点赞消息, action);

        /// <summary>
        /// 当收到进直播间消息时执行回调
        /// </summary>
        public void IfMemberMsg(Action<MemberMessage> action) => IfTypedMsg(PackMsgType.进直播间, action);

        /// <summary>
        /// 当收到关注消息时执行回调
        /// </summary>
        public void IfFollowMsg(Action<Msg> action) => IfTypedMsg(PackMsgType.关注消息, action);

        /// <summary>
        /// 当收到礼物消息时执行回调
        /// </summary>
        public void IfGiftMsg(Action<GiftMsg> action) => IfTypedMsg(PackMsgType.礼物消息, action);

        /// <summary>
        /// 当收到直播间统计消息时执行回调
        /// </summary>
        public void IfUserSeqMsg(Action<UserSeqMsg> action) => IfTypedMsg(PackMsgType.直播间统计, action);

        /// <summary>
        /// 当收到粉丝团消息时执行回调
        /// </summary>
        public void IfFansclubMsg(Action<FansclubMsg> action) => IfTypedMsg(PackMsgType.粉丝团消息, action);

        /// <summary>
        /// 当收到直播间分享消息时执行回调
        /// </summary>
        public void IfShareMsg(Action<ShareMessage> action) => IfTypedMsg(PackMsgType.直播间分享, action);

        /// <summary>
        /// 当收到下播消息
        /// </summary>
        public void IfLiveEndMsg(Action<Msg> action) => IfTypedMsg(PackMsgType.下播, action);

        /// <summary>
        /// 解析所有未知类型的消息
        /// </summary>
        public void IfAnyMsg(Action<Msg> action)
        {            
            // 将 Data 转换为 JObject
            JObject jObject = null;
            try
            {
                jObject = JObject.Parse(this.Data);
            }
            catch (Exception)
            {
                // 解析失败，忽略错误
                return;
            }

            // 如果成功解析为 JObject，转换为 Msg 对象
            if (jObject != null)
            {
                var msg = jObject.ToObject<Msg>();
                if (msg != null)
                {
                    action(msg);
                }
            }
        }

        /// <summary>
        /// 泛型方法，根据 PackMsgType 解析消息对象并执行回调
        /// </summary>
        private void IfTypedMsg<T>(PackMsgType expectedType, Action<T> action) where T : Msg
        {
            if (this.Type != expectedType) return;
            if (this.Data == null) return;

            // 尝试将 Data 直接作为 T 类型使用
            T msg = null;

            try
            {
                // 如果 Data 是字符串类型
                if (this.Data is string)
                {
                    // 先尝试解析为 JObject
                    try
                    {
                        JObject jObject = JObject.Parse(this.Data);
                        msg = jObject.ToObject<T>();
                    }
                    catch
                    {
                        // 如果解析 JObject 失败，尝试直接反序列化
                        msg = JsonConvert.DeserializeObject<T>(this.Data);
                    }
                }
            }
            catch (Exception)
            {
                // 解析失败，忽略错误
            }

            // 如果成功解析消息，执行回调
            if (msg != null)
            {
                action(msg);
            }
        }
    }

    /// <summary>
    /// 消息
    /// </summary>
    public class Msg
    {
        /// <summary>
        /// 弹幕ID
        /// </summary>
        public long MsgId { get; set; }

        /// <summary>
        /// 用户数据
        /// </summary>
        public MsgUser User { get; set; }

        /// <summary>
        /// 主播简要信息
        /// </summary>
        public RoomAnchorInfo Owner { get; set; }
        public string Onwer { get; set; } = "该字段存在拼写错误，请修正为 ‘Owner’ 后使用";

        /// <summary>
        /// 消息内容
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// 房间号
        /// </summary>
        public string RoomId { get; set; }

        /// <summary>
        /// web直播间ID
        /// </summary>
        public string WebRoomId { get; set; }

        /// <summary>
        /// 房间标题
        /// </summary>
        public string RoomTitle { get; set; }

        /// <summary>
        /// 是否是匿名直播间
        /// </summary>
        public bool IsAnonymous { get; set; }

        /// <summary>
        /// 用户使用的 Appid ，已知 1128，8663，2329 等
        /// </summary>
        public string Appid { get; set; }
    }

    /// <summary>
    /// 粉丝团信息
    /// </summary>
    public class FansClubInfo
    {
        /// <summary>
        /// 粉丝团名称
        /// </summary>
        public string ClubName { get; set; }

        /// <summary>
        /// 粉丝团等级，没加入则0
        /// </summary>
        public int Level { get; set; }
    }

    /// <summary>
    /// 直播间主播信息
    /// </summary>
    public class RoomAnchorInfo
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// SecUid
        /// </summary>
        public string SecUid { get; set; }

        /// <summary>
        /// 昵称
        /// </summary>
        public string Nickname { get; set; }

        /// <summary>
        /// 头像地址
        /// </summary>
        public string HeadUrl { get; set; }

        /// <summary>
        /// 关注状态 0未关注,1已关注,...
        /// </summary>
        public int FollowStatus { get; set; }
    }

    /// <summary>
    /// 用户弹幕信息
    /// </summary>
    public class MsgUser
    {
        /// <summary>
        /// 真实ID
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 是否是直播间管理员
        /// </summary>
        public bool IsAdmin { get; set; } = false;

        /// <summary>
        /// 是否是主播自己
        /// </summary>
        public bool IsAnchor { get; set; } = false;

        /// <summary>
        /// ShortId
        /// </summary>
        public long ShortId { get; set; }

        /// <summary>
        /// 自定义ID
        /// </summary>
        public string DisplayId { get; set; }

        /// <summary>
        /// 昵称
        /// </summary>
        public string Nickname { get; set; }

        /// <summary>
        /// 未知
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        /// 支付等级
        /// </summary>
        public int PayLevel { get; set; }

        /// <summary>
        /// 性别 1男 2女
        /// </summary>
        public int Gender { get; set; }

        /// <summary>
        /// 头像地址
        /// </summary>
        public string HeadImgUrl { get; set; }

        /// <summary>
        /// 用户主页地址
        /// </summary>
        public string SecUid { get; set; }

        /// <summary>
        /// 粉丝团信息
        /// </summary>
        public FansClubInfo FansClub { get; set; }

        /// <summary>
        /// 粉丝数
        /// </summary>
        public long FollowerCount { get; set; }

        /// <summary>
        /// 关注状态 0 未关注 1 已关注 2,不明
        /// </summary>
        public long FollowStatus { get; set; }

        /// <summary>
        /// 关注数
        /// </summary>
        public long FollowingCount;


        public string GenderToString()
        {
            return Gender == 1 ? "男" : Gender == 2 ? "女" : "妖";
        }
    }

    /// <summary>
    /// 礼物消息
    /// </summary>
    public class GiftMsg : Msg
    {
        /// <summary>
        /// 礼物ID
        /// </summary>
        public long GiftId { get; set; }

        /// <summary>
        /// 礼物名称
        /// </summary>
        public string GiftName { get; set; }

        /// <summary>
        /// 礼物分组ID
        /// </summary>
        public long GroupId { get; set; }

        /// <summary>
        /// 本次(增量)礼物数量
        /// </summary>
        public long GiftCount { get; set; }

        /// <summary>
        /// 礼物数量(连续的)
        /// </summary>
        public long RepeatCount { get; set; }

        /// <summary>
        /// 抖币价格
        /// </summary>
        public int DiamondCount { get; set; }

        /// <summary>
        /// 该礼物是否可连击
        /// </summary>
        public bool Combo { get; set; }

        /// <summary>
        /// 礼物图片地址
        /// </summary>
        public string ImgUrl { get; set; }

        /// <summary>
        /// 送礼目标(连麦直播间有用)
        /// </summary>
        public MsgUser ToUser { get; set; }
    }

    /// <summary>
    /// 点赞消息
    /// </summary>
    public class LikeMsg : Msg
    {
        /// <summary>
        /// 点赞数量
        /// </summary>
        public long Count { get; set; }

        /// <summary>
        /// 总共点赞数量
        /// </summary>
        public long Total { get; set; }
    }

    /// <summary>
    /// 直播间统计消息
    /// </summary>
    public class UserSeqMsg : Msg
    {
        /// <summary>
        /// 当前直播间用户数量
        /// </summary>
        public long OnlineUserCount { get; set; }

        /// <summary>
        /// 累计直播间用户数量
        /// </summary>
        public long TotalUserCount { get; set; }

        /// <summary>
        /// 累计直播间用户数量 显示文本
        /// </summary>
        public string TotalUserCountStr { get; set; }

        /// <summary>
        /// 当前直播间用户数量 显示文本
        /// </summary>
        public string OnlineUserCountStr { get; set; }
    }

    /// <summary>
    /// 粉丝团消息
    /// </summary>
    public class FansclubMsg : Msg
    {
        /// <summary>
        /// 粉丝团消息类型,升级1，加入2
        /// </summary>
        public int Type { get; set; }

        /// <summary>
        /// 粉丝团等级
        /// </summary>
        public int Level { get; set; }
    }

    /// <summary>
    /// 来了消息
    /// </summary>
    public class MemberMessage : Msg
    {
        /// <summary>
        /// 当前直播间人数
        /// </summary>
        public long CurrentCount { get; set; }

        /// <summary>
        /// 直播间进入方式，目前已知 0 正常进入，6 通过分享进入
        /// </summary>
        public long EnterTipType { get; set; }
    }

    /// <summary>
    /// 直播间分享
    /// </summary>
    public class ShareMessage : Msg
    {
        /// <summary>
        /// 分享目标
        /// </summary>
        public ShareType ShareType { get; set; }
    }
}