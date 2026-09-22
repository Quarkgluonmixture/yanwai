# LOG — 发生了什么 + 为什么

append-only。标题行打标签，用 `grep -n '^## ' LOG.md` 出目录。

标签：`#decision` 决定做/不做/回退 · `#measure` 测量结果（必带 n / 日期 / 怎么复现）·
`#deadend` 试过不行 · `#incident` 踩坑 · `#ship` 落地。

## [2026-09-22] Fork 并改名为 言外 / Yanwai  #decision #ship
- 原仓库 Wionerlol/wechat-jev-hud 我只有读权限，且**没有 LICENSE**（默认保留所有权利）。
  ⇒ 选 fork 而不是新建仓库：保留出处和 commit 历史，不碰授权问题。
- 当初是 `--depth 1` 克隆的，历史只有 1 个 commit。fork 后完整 clone 拿回 22 个，
  再把本地 5 个 commit fetch 过来，祖先链仍接在原始 first commit 上。⇒ 坑 9。
- 只替换 `WeChatJevHud` 这一个 token。`WeChat` 本身指真实应用
  （`WeChatWindowSnapshot` / `Win32WeChatWindowTracker` / `MMUIRenderSubWindow`），不能动。
- 私有 SDK 目录跟着改名搬到 %LOCALAPPDATA%\Yanwai\dotnet。不搬的话脚本会静默退回
  系统 9.0.101，然后收到一条看不出根因的 global.json 报错。⇒ 坑 7。
- 搬的时候被 20 个残留 dotnet 编译服务器卡住，半途失败。先 build-server shutdown 再搬。⇒ 坑 6。

## [2026-09-22] 按编码习惯过一遍代码，修掉一处静默降级  #incident
- `Win32WeChatWindowTracker.EnumerateCandidates` 在 EnumWindows 回调里 `catch (Exception) {}`，
  注释说是防窗口枚举期间消失——但那种情况 `TryCreateCandidate` 内部已经接住了。
  所以外层能吞到的按构造只剩真 bug，而它一声不吭 ⇒ 微信永远找不到且无线索。
- 不能改成往上抛：异常逃出 P/Invoke 回调会撕掉进程。⇒ 改成**吞了但记下来**
  （计数 + 第一条），调用方在「没找到窗口」时一并报出。⇒ 坑 8。
- 审计的其余结论：Python 脚本到处显式 `encoding="utf-8"`，干净；
  `SystemOneClient` 把空 key 和未设置 key 同等对待且有测试覆盖，符合「空字符串不是未设置」。
  `Diagnostics` 的 ReadAllText/WriteAllText 没写显式编码，功能无碍，记进 TODO。

## [2026-09-22] 建档：CHECKPOINT / TODO / LOG / GOTCHAS  #decision
- 仓库已有自己的 docs 约定（VISION/DECISIONS/SPEC/EXAMPLES/ACCEPTANCE/CONTEXT）。
  ⇒ 不套模板、只补缺的四件；`docs/ACCEPTANCE.md` 继续当阶段唯一真相，快照只放一行 cursor。
- GOTCHAS 直接拆出来而不是先塞进快照：这一轮攒下 9 条耐久坑，塞进去必然把快照顶爆。
