---
name: wireshark-mcp
description: 通过 Wireshark (TShark) 解析 pcapng 抓包文件并通过 MCP 暴露给 LLM。在用户需要深入分析网络数据包内容（如快手/抖音加密流量的二进制协议）时调用。
---

# Wireshark MCP 网络分析助手

此技能允许通过 `bx33661/Wireshark-MCP` 项目，将 Wireshark 强大的网络数据包解析能力开放给大语言模型，实现 pcap/pcapng 文件的自动分析和检索。

## 使用场景

1.  **深入流量分析**：当内置抓包工具（如 TitaniumProxy）无法解开或拦截的数据包，需要深入到 TCP/IP 或原始 Payload 层分析时。
2.  **提取协议字段**：当需要批量从 `.pcap` 或 `.pcapng` 文件中提取特定的 HTTP, WebSocket, TCP, UDP 字段时。
3.  **加密流量破译**：利用 TShark 配合密钥日志进行解密分析，或者查找二进制流量中的特定 Hex 特征。

## 先决条件

*   **系统**：macOS 或 Windows（需要在能执行抓包分析的机器上）
*   **依赖**：Node.js
*   **工具**：Wireshark（安装了 `tshark` 命令行工具）

## 核心功能 (MCP Tools)

*   `read_pcap(...)`: 指定 `pcapng` 文件路径以及 Wireshark 过滤表达式（如 `tcp.port == 443 and ws`），让 TShark 将过滤后的数据包结构化为 JSON 供 LLM 分析。

## 典型提示词

"请使用 Wireshark MCP 读取我桌面上的 `kuaishou_dump.pcapng`，过滤条件为 `tcp.port == 443`，帮我找出其中带有 GZIP 压缩头 `1F 8B 08` 的 WebSocket Payload。"