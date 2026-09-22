# TODO — 只放未来的活

做完的条目**立即删除**，不留 done 列表（历史在 `LOG.md`）。
阶段验收条目的唯一真相是 `docs/ACCEPTANCE.md`，这里只放跨阶段或阶段外的活。

## Phase 6 后半（浮层，最要紧）

- [ ] **真机验证批注卡片**：真实微信里气泡下方很少有空位，大多会落在右侧——看右侧卡片挡不挡
      自己发的长消息、退成小标签的频率高不高。`--overlay-demo` 改走 board 后也没在真机重看过。
- [ ] **短消息 OCR**：三四个字的消息 Tesseract 常给 LowConfidence，被整条跳过。
      Phase 3 实测 Windows OCR 在短中文上最强但没有置信度 ⇒ 这是接 WeChatOCR 或换 Paddle 的主要理由。
- [ ] **真机验证 fallback 防护**。只在 `PrintWindow` 失败时触发，本机从没退化过，没法主动造。
      代码路径：第一帧 fallback 丢弃并隐藏浮层，之后 fallback 帧照常观察但浮层不显示。
- [ ] **debug 展开视图**：耗时、detection score、OCR 置信度、Jev 概率四类数各自分开显示。
      `docs/ACCEPTANCE.md` 明确要求这四个不能混成一个数。
- [ ] **跨显示器 / DPI 切换真机验证**。代码路径写了、`OverlayPlacementTests` 覆盖了几何，
      但本机只有一块屏，从没在真实的双屏 + 不同缩放下跑过。

## 判定质量

- [ ] 用几天，记录哪些问题问得不对。问题集在 `src/Yanwai.TypeSafe/ConversationQuestionSet.cs`
      一个文件里，改起来便宜。
- [ ] 观察「看不出来 / 说不好」这个出口被选中的频率。如果某个问题经常落到它，
      说明选项划分错了（不是 Jev 不行——见 `docs/GOTCHAS.md` 的元规律一节）。
- [ ] 熟人打趣类对话是已知的弱项（上游那个失败项目的 `KNOWN_ISSUES.md` 有 11 条实测）。
      攒够样本再决定要不要为这一类单独加问题。

## 工程

- [ ] `scripts/build.ps1` 加一个 pre-step：构建前自动停掉还开着的 `Yanwai.Panel`
      （见 `docs/GOTCHAS.md` 坑 6，现在只能靠人记得）。
- [ ] `Yanwai.Diagnostics` 的 `File.ReadAllTextAsync` / `WriteAllTextAsync` 没写显式编码。
      .NET 默认就是 UTF-8 无 BOM，功能上没问题，但按编码边界的规矩应该写死。
- [ ] 考虑接微信自带的 `WeChatOCR` 进程替代 Tesseract——本机一直跑着，中文准确率应该更高。
- [ ] 决定 `C:\Workspace\wechat-jev-hud`（改名前的浅克隆）留不留。里面的 5 个 commit
      已经完整搬到本仓库，浅克隆本身没有额外价值。
