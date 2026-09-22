# CLAUDE.md — 言外 / Yanwai

这个项目的协作契约在 **`AGENTS.md`**，Codex 和 Claude 走同一份，⛔ 不在这里复述。
全局编码纪律由 claude-kit 的 rules 层提供，⛔ 也不在这里抄——抄一份就是又一份会漂的副本。

这里只放 Claude Code 在**这个仓库**里特有的东西。

## 接手顺序

1. `CHECKPOINT.md` — 唯一入口，通常读它就够了
2. `docs/GOTCHAS.md` — **动手前扫**。9 条耐久坑，顶部有「你正在哪一步」的挑读表
3. `TODO.md` — 未完的活
4. `AGENTS.md` — 改代码前的完整契约（含决策优先级和硬禁令）

阶段进度的唯一真相是 `docs/ACCEPTANCE.md`，⛔ 别在别处复制 stages。

## 这个仓库的构建不能直接敲 `dotnet`

`global.json` 钉死 SDK 8.0.425，系统装的是 9.0.101。走脚本：

```powershell
.\scripts\build.ps1
.\scripts\panel.ps1
```

脚本会优先用 `%LOCALAPPDATA%\Yanwai\dotnet\dotnet.exe`（不在 PATH 上）。细节见坑 7。

**改完代码构建前先停掉还开着的 `Yanwai.Panel`**，否则 MSB3027 锁文件失败。见坑 6。

## 验证这个项目的东西，截图是必须的

UI / 浮层 / 样式的改动 **grep 证明不了**（声明存在 ≠ 它在级联里赢，见坑 2）。
面板和 HUD 都能用 UI Automation 驱动 + 截图核对，这一轮就是这么抓到两个 bug 的：

```powershell
# 驱动面板：AutomationId 是 ConversationBox / AnalyzeButton / LiveToggle /
# StatusText / TargetText / DangerText / AdviceText
```

浮层跟随行为用 `--overlay-demo` 看，它不调 Jev：

```powershell
& "src\Yanwai.Panel\bin\Debug\net8.0-windows10.0.19041.0\Yanwai.Panel.exe" --overlay-demo
```

## 三条不能碰的线

`AGENTS.md` 里有完整版，这三条最容易在「顺手优化一下」的时候破：

- ⛔ 不注入微信、不 hook、不读本地加密数据库、不自动回复
- ⛔ 聊天原文不落盘；`TYPESAFE_API_KEY` 不进仓库 / 日志 / 截图
- ⛔ 问题集只问可观察的对话行为，不问人品和内心状态
