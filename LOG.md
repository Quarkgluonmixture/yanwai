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

## [2026-09-23] 浮层跟随滚动 + fallback 帧不喂浮层  #ship
- observer 每帧重建 `State.VisibleMessages`（逻辑 id → 这一帧的气泡框），滚动时同一条消息 id 不变。
  ⇒ watcher 每帧把这张表发出来，面板按「被判定那条的 id」查位置，查不到就隐藏但**保留内容**，
  滚回来自动重新出现。没往 observer 里加任何东西。
- Jev 要近一秒，结果回来时气泡可能已经动了 ⇒ Show 之后立刻用最新一帧的位置再对齐一次。
- fallback（桌面拷贝）会把浮层自己拍进帧里：第一帧 fallback 直接丢弃并隐藏浮层，
  后续 fallback 帧照常观察，但位置不给浮层用。选「拒绝显示」而不是「先隐藏再抓」，
  因为后者要跨线程等 UI 隐藏完成，而 fallback 在本机从没出现过，不值得这份复杂度。
- 新增单测 `Visible_messages_track_a_live_message_through_scroll_away_and_back`：
  抓的是「observer 不刷新框 / 离屏 id 还挂在表里」⇒ 浮层钉在旧位置。
- 顺手发现 `.ocr-cache/tessdata` 不在了（改名搬目录时没跟着走，它是 gitignored），
  已用 `scripts/install-ocr-models.ps1` 按钉死的 revision 重下，大小与旧克隆一致。

## [2026-09-23] 「判定最后一条」按钮：不用等新消息  #ship
- 开启前就在屏幕上的消息是基线，按设计永不自动触发（坑 4）⇒ 加一个显式按钮来判定已有消息。
  请求只置一个 volatile 标志，由 watcher 自己的循环线程在下一帧处理，不从 UI 线程读 observer 状态。
- 第一版只盯最后一条，真机上最后一条恰好 OCR LowConfidence 被拒 ⇒ 改成从下往上找
  **最近一条可信的对方消息**，并报出跳过了几条。浮层锚到被判的那条，不会贴错气泡。
- 真机：判定 780 ms、5 项判定、1114 in / 253 out；那屏有 5 条因 OCR 不可信没进上下文
  ——OCR 质量现在是判定的主要瓶颈（见 TODO 里 WeChatOCR 那条）。

## [2026-09-23] 一屏都判：每条一个小标签 + 选中那条一张大卡  #ship
- 用户反馈「只有这一个，上下那些怎么看」。单张浮层 → `OverlayBoard`：选中的消息一张完整卡片，
  其余判定过的消息各一个一行小标签（建议 + 概率，圆点颜色 = 危险等级，没有危险答案时是灰色，不是绿色）。
  会被大卡片盖住的小标签直接不画。布局是纯函数 `OverlayLayout.Arrange`，有 3 条单测。
- 「判定这一屏」变成开关：打开后**滚到哪判到哪**，watcher 每帧把没交过的可读对方消息交给面板，
  面板串行排队问 Jev（新到的消息插队）。判过的按 id 缓存，滚回来不重复调用。
- 面板加「这一屏的判定」列表，点哪条看哪条的详情，浮层的大卡片跟着移过去。切会话时清空。
- 真机：当时屏幕上对方只有 2 条，1 条 3 个字 OCR LowConfidence ⇒ 只出 1 张。短消息 OCR 仍是瓶颈。
- 删掉单卡片的 `IOverlayPresenter` / `WpfOverlayPresenter`，`--overlay-demo` 改走 board（没在真机重看过）。

## [2026-09-23] HUD 改成「写在消息下面的批注」样式  #decision #ship
- 用户给了一张手机微信的效果图：每条对方消息下面一块灰底批注，「Jev：」+ 问题 + `- 选项：xx%`。
- 做不到的那一半：图里批注是**插进聊天流**、把后面的消息往下推的。那要改微信的布局 = 注入，⛔。
  ⇒ 位置规则：气泡正下方有空就放下方（像图里）；不然放气泡右侧空白处；都会压到别的气泡
  （任意一方的）或别的卡片就退成一行小标签；再不行就不画。选中的先放，然后从新到旧。
  规则在纯函数 `OverlayLayout.Arrange`，5 条单测。
- 卡片高度按内容实测（WPF Measure），不是写死的，因为「放不放得下」取决于它。
  离屏渲染核对过：4 节时 270 DIP 太高 ⇒ 浮层只留 话里有话 / 怎么回（前 3 项）/ 危险等级，
  心情和「她要什么」只在面板。
- 配色按用户微信的深色主题取灰，不照抄效果图的浅色。

## [2026-09-23] OCR 换成 PP-OCRv6 small（ONNX）+ 两引擎核对；核不上的也判，但标出来  #decision #ship #measure
- 起因：用户问「为什么只有一个气泡」。脱敏观察：一屏 11 个气泡只有 3 个 OCR 可信，对方的 2 条里 1 条不可信；
  同一条 3 字消息两次运行结果不同。旧组合（Tesseract ≥0.90 否则 Windows OCR 标 LowConfidence）是瓶颈。
- 选型：Phase 3 已测 PP-OCRv6 small 短中文 10/11（旧组合 7/11）。本机没有 Paddle Python 环境，
  HF 上有官方 `PaddlePaddle/PP-OCRv6_small_rec_onnx`（2026-06 发布，过 7 天冷却）⇒ .NET 直接跑 ONNX Runtime 1.30.0，
  不引 Python sidecar。模型钉 revision b8f84f0，onnx 的 sha256 与 HF LFS 记录一致，安装脚本校验。
- 公开夹具（phase3-public.json，n=5）逐步：整块气泡直接喂 1/5 → 逐行紧裁剪 4/5（多行不再截断、首字不再被气泡尾巴吞）
  → 唯一错的是「诶」：**字典里没有这个字**，模型给 1.00 置信度直接丢字。
- 信任规则（`CrossCheckedOcrEngine`）：先要求逐字一致 ⇒ Windows OCR 把「怎么说」读成「怎久说」、「sos」读成「s。s」，
  正确的读法被更差的核对方否决。改成差 ≤20%（至少容 1 字）即采纳主引擎；缺的字主引擎输出不了就采纳核对方。
  核对方改喂逐行、白底黑字的裁剪（`LineWiseOcrEngine`）：深底白字的对方气泡原来整条返回空。
  结果：公开夹具 5/5 认对且 5/5 核实。
- 真屏幕（175% DPI，4 个气泡，`--ocr-compare-live`，不打印原文）：2/4 核实；另 2 条 Windows 读出的汉字和 Paddle 大面积不同，
  谁对不知道（看不到原文）。⇒ 决定：**核不上的也判**，卡片顶上写「OCR 未核实，判的是：「…」」，列表加前缀，状态栏计数。
  这放宽了 Phase 3「不可信文字不进语义」的做法，理由：ACCEPTANCE 要求的是「显式标为不确定或跳过，不许悄悄当真」，
  标出来并把原文摆在判定旁边满足前者；而跳过让一屏大半消息消失，用户直接说「不能都显示」。
- Tesseract 退出实时路径（面板不再引用），只留在 `--ocr-evaluate` 里做对照。
