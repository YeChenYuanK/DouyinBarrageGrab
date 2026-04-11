# JSReverser-MCP 快速接入（DouyinBarrageGrab）

## 为什么现在就要接
- 我们当前快手链路已经能抓到大包，但“弹幕正文稳定字段路径”还需要更快定位
- 该工具可以在真实浏览器会话下直接观察 WebSocket 帧与调用链，减少盲猜
- 对本项目最直接价值是：快速锁定“评论正文在哪个消息分组/字段里”

## 本项目优先使用场景
- 快手网页版直播间：`live.kuaishou.com`
- 目标连接：`wss://livejs-ws.kuaishou.cn/group*`
- 目标结果：稳定提取 `nickname + content + timestamp`

## 最短落地流程
1. 在支持 MCP 的客户端中接入 JSReverser-MCP
2. 打开已登录的 Chrome 页面并进入目标直播间
3. 按下面顺序跑最小链路：
   - 列出 websocket 连接
   - 选择目标 wsid
   - 按消息特征分组
   - 拉取分组消息摘要
   - 锁定评论消息分组
4. 把分组证据回灌到本项目 `WssBarrageGrab.cs` 的快手解析分支

## 实战操作模板
- 先做页面健康检查，确认浏览器可控
- 列出 websocket 连接，定位 `livejs-ws.kuaishou.cn`
- 分析消息分组，按长度/方向/频率先分离心跳与业务包
- 对业务分组抽样，找可读昵称、评论文本、时间戳字段
- 如字段不直观，再做最小 Hook 追参数生成函数

## 证据记录格式（建议）
- 页面 URL：
- wsid：
- 分组 ID：
- 帧方向（send/receive）：
- 样本帧长度分布：
- 命中的昵称字段路径：
- 命中的正文字段路径：
- 命中的时间字段路径：
- 反序列化方式说明：

## 回灌到本项目的改动点
- 快手入口：`BarrageGrab/Server/WssBarrageGrab.cs`
- 协议实体：`BarrageGrab/Modles/ProtoEntity/KsMessages.cs`
- 日志验收关键字：`KS_BIZ_PACKET`、`[快手][Fallback][BIZ_PACKET]`

## 本地先验命令（现有脚本）
```bash
python3 scripts/ks_ws_live_client.py \
  --har '/Users/yechenyuan/Dev/Unity/DouyinBarrageGrab/快手 协议ws 样本/快手 - 实时 弹幕' \
  --offline-chat-scan \
  --offline-max-messages 400 \
  --offline-max-hits 30
```

## 发布流程提醒（必须遵守）
1. Mac 改代码并 push
2. Win pull（带 `-c http.proxy=http://127.0.0.1:7897`）+ Build.bat
3. 重启 `E:\DouyinBarrageGrab\Output\WssBarrageServer.exe` 验收
