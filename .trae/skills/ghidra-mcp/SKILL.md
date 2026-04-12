---
name: "ghidra-mcp"
description: "通过 Ghidra 的无头模式提取二进制分析数据到 JSON，并通过 MCP 暴露给 LLM。在用户需要对大型二进制文件（如 exe）进行结构化、自动化的逆向分析时调用。"
---

# Ghidra MCP 逆向助手

此技能允许通过 `Bamimore-Tomi/ghidra_mcp` 项目，将 Ghidra 作为一个后端的二进制数据提取器，把 C/C++ 结构体、函数伪代码、枚举等信息提供给大语言模型，辅助复杂的逆向工程。

## 使用场景

1.  **大规模逆向工程**：当遇到未知的二进制协议、混淆的客户端（如 `kwailive.exe`）时。
2.  **获取代码结构**：需要知道目标程序中所有函数的列表、特定函数的 C 伪代码。
3.  **获取数据结构**：需要提取目标程序中的 Struct 或 Enum 定义。

## 先决条件

*   **系统**：macOS (测试机)
*   **依赖**：Python 3.10+, Java 21 (`temurin@21`)
*   **工具**：Ghidra 11.3.1+, MCP CLI (`uv` 或 `pip`)

## 核心工具箱 (MCP Tools)

*   `setup_context(...)`: 在指定的二进制文件上运行 Ghidra 的无头分析，生成基础的 `ghidra_context.json`。
*   `list_functions()`: 列出二进制文件中的所有函数。
*   `get_pseudocode(name)`: 获取指定函数的反编译伪代码。
*   `list_structures()` / `get_structure(name)`: 获取结构体信息。
*   `list_enums()` / `get_enum(name)`: 获取枚举值。

## 典型提示词

"请使用位于 `<GHIDRA_PATH>` 的 Ghidra 分析位于 `<BINARY_PATH>` 的二进制文件。首先，设置分析上下文，然后列出所有函数，重点查找包含 'WebSocket' 或 'Decrypt' 关键词的函数伪代码。"