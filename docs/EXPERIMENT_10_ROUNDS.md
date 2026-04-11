# 10轮盲测实验模板

## 目标
- 验证方法论是否可行：不依赖先验域名，盲测能否稳定收敛并复现。

## 前置条件
- 使用最新 `WssBarrageServer.exe`。
- 每轮统一动作窗口：开播 `5-8` 秒，下播后再等 `2-3` 秒。
- 每轮都执行：`开始抓包(SNI)` -> 操作开播/下播 -> `停止并分析SNI`。

## 执行记录表
每轮记录如下（可复制 10 次）：

```text
轮次: 1
开始时间:
结束时间:
traceId:
备注(异常/误操作):
```

## 自动产物
- `E:\DouyinBarrageGrab\Output\logs\live_control_signal.jsonl`
- `E:\DouyinBarrageGrab\Output\logs\blind_control_signal.jsonl`

## 自动评估命令
在 Windows PowerShell 执行：

```powershell
python E:\DouyinBarrageGrab\scripts\live_control_regression_report.py --last 10
```

## 阈值（建议）
- `blind_top3_consistency >= 80%`
- `confirmed + weak >= 90%`
- `no_hit <= 10%`

同时满足则可判定方法可行（PASS）。
