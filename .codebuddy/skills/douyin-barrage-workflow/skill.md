---
name: douyin-barrage-workflow
description: DouyinBarrageGrab（抖音/快手弹幕抓取工具）的开发和测试工作流。用于编译、调试、打包流程，以及常见编译错误的解决方案。当用户提到"抖音弹幕"、"快手弹幕"、"弹幕抓取"、"编译EXE"、或需要为该项目编写提交日志时触发此 skill。
---

# DouyinBarrageGrab 工作流

## ⚠️ 铁律：每次修改必须严格按此流程执行，禁止任何捷径（如 scp 传文件）

---

## 完整开发测试流程（三步，缺一不可）

### 第一步：Mac 修改代码并提交推送
1. 在 `/Users/yechenyuan/Dev/Unity/DouyinBarrageGrab` 修改代码
2. `git add <files> && git commit -m "fix/feat: <描述>"`
3. `git push`
4. **必须确认 push 成功后才能进行下一步**

### 第二步：AI 通过 SSH 在 Win 测试机执行 pull + 编译
```bash
# git pull（使用 GitHub Desktop 自带 git）
ssh wuhan@192.168.3.55 '"C:\Users\wuhan\AppData\Local\GitHubDesktop\app-3.5.7\resources\app\git\cmd\git.exe" -C E:\DouyinBarrageGrab pull'

# 编译
ssh wuhan@192.168.3.55 'powershell -ExecutionPolicy Bypass -Command "cd E:\DouyinBarrageGrab; .\Build.bat"'
```
- **这一步由 AI 来做，不是让用户手动操作**
- 如果 git pull 报 local changes 冲突，先 checkout 再 pull：
  ```bash
  ssh wuhan@192.168.3.55 '"C:\Users\wuhan\AppData\Local\GitHubDesktop\app-3.5.7\resources\app\git\cmd\git.exe" -C E:\DouyinBarrageGrab checkout -- BarrageGrab/Proxy/TitaniumProxy.cs'
  ```

### 第三步：用户重启工具验收
- 用户重启 `E:\DouyinBarrageGrab\Output\WssBarrageServer.exe`
- 用户测试后反馈结果

---

## Win 测试机信息

| 项目 | 值 |
|------|-----|
| IP | `192.168.3.55` |
| 用户 | `wuhan` |
| 项目路径 | `E:\DouyinBarrageGrab` |
| git 路径 | `C:\Users\wuhan\AppData\Local\GitHubDesktop\app-3.5.7\resources\app\git\cmd\git.exe` |
| MSBuild 路径 | `D:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe` |
| 编译脚本 | `E:\DouyinBarrageGrab\Build.bat` |
| 输出 EXE | `E:\DouyinBarrageGrab\Output\WssBarrageServer.exe` |

---

## 项目基本信息

| 项目名 | DouyinBarrageGrab（弹幕抓取工具） |
|--------|-----------------------------------|
| Mac 路径 | `/Users/yechenyuan/Dev/Unity/DouyinBarrageGrab` |
| 框架 | .NET Framework 4.6.2 |
| 语言版本 | C# 7.3 |

---

## 常见编译错误及解决方案

### 错误 #1：KsMessages.cs 未加入项目
- **症状**：CS0246，找不到 `KsChatMessage`、`KsGiftMessage` 等类型
- **根因**：`.csproj` 缺少 `<Compile Include="Modles\ProtoEntity\KsMessages.cs" />`
- **修复**：在 `WssBarrageService.csproj` 中添加引用

### 错误 #2：Fleck API 不兼容
- **症状**：CS1061 `OnConnect` 未定义
- **根因**：Fleck 1.2.0 已移除 `OnConnect` 事件
- **修复**：
  ```csharp
  // 旧：
  socketServer.OnConnect += Listen;
  socketServer.Start();
  // 新：
  socketServer.Start(Listen);
  ```

### 错误 #3：C# 8.0 using 声明
- **症状**：CS8370，功能"Using 声明"在 C# 7.3 中不可用
- **修复**：改用传统嵌套 using：
  ```csharp
  using (var ms = new MemoryStream(data))
  using (var gz = new GZipStream(ms, CompressionMode.Decompress))
  using (var out_ = new MemoryStream())
  {
      gz.CopyTo(out_);
      return out_.ToArray();
  }
  ```

---

## Git 提交规范

```bash
git add <files>
git commit -m "fix: <描述>"   # bug 修复
git commit -m "feat: <描述>"  # 新功能
git commit -m "docs: <描述>"  # 文档
git push
```
