# 菜单首态本地试修 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Execute the single coupled code/test task, then independent review and local packaging. Existing explicit execution authorization applies; do not ask again.

**Goal:** 在 Show 前准备菜单动画首态，显示后只启动同代次时钟，交付可审阅本地候选与准确证明范围。

**Architecture:** 复用 TrayMenuWindow 的 generation、取消和布局路径。拆开首态准备与动画启动，保留同步重入与关闭中 toggle 语义。测试先行，同一协议保存冻结基线与修复后的真实托管表面状态、布局及离屏视觉；不宣称实际DWM呈现。

**Tech Stack:** C#/.NET10、WPF、xUnit、现有离屏预览、既有便携 Inno Setup6.4.1。

**Spec:** PI-MENU-FIRST-FRAME-TRIAL-008 v1 用户授权（本文件下列范围），DIAG-007 `artifacts/reliability/menu-double-open/report.md` 为已核实依据，不是已定因证据。

## Global Constraints

- 工作目录 C:/Users/TomatoK/.codex/worktrees/reliability/CodexQuotaTaskbar，基线 e8d21173b90ef47d5f3687f6293f4979b8bcaed8 / codex/reliability；保留 main25M8U/43文件/index、其他分支、正式安装和六配置、历史五项成果及原产物。
- 禁止推送/发布/安装/替换/启动产品App主入口、桥接、训练、账号读取、真实消息、正式配置写入、生产鼠标/输入/抢焦点/影响工作区的窗口与录像。只许隔离测试设置，无网络依赖；不执行更新沙盒driver/dry-run/故障恢复。
- 不清理 perf/fixtures（曾被拒）；不改模型/账号/权限/安全/登录设置，不恢复工作台或每日交接。旧源码全部由Git基线和冻结副本保留。
- 不固定防抖、不盲等130ms。现有 PlayPopupEntrance 依赖 IsVisible，不能整体提前；拆职责，减少动画/系统禁动画首态1。
- 当前授权允许非侵扰隔离运行，不授权可见生产窗口验证。CompositionTarget.Rendering 仅WPF回调；RenderTargetBitmap/合成视频仅离屏/合成，均非DWM实际首帧。 无Show/无HWND方案只能证明准备边界属性与静态调用链；可见且Loaded后的实际单次时钟与首帧必须单列未测，不称通过。
- 单一实施者写生产和测试，根负责保护、文档、包装、最终本地提交与报告；审查者只读。子agent不得再派agent。

## Review Focus

- 首次与已加载复用打开：显示后不再从1归零，不重复同代次启动。
- 关闭中再次toggle完成关闭；旧入口/退场回调不误关或重开后续代次。
- reduce/system motion关闭或中途改变：完整可用，不永远透明。
- QuotaPopover左详情与Dot/会话左面板身份分开；快速输入与焦点/捕获语义保留。
- 四方向、DPI96/144/192、边缘不足空间：位置先准备，重排不重播入场。

### Task 1: 拆分首态与时钟，测试与前后证据

**Files:**
- Modify: src/CodexQuotaTaskbar.Host/UI/TrayMenuWindow.PopupMotion.cs、TrayMenuWindow.xaml.cs、TrayMenuWindow.Placement.cs（仅所需路径；若其他必需文件先报根）。
- Modify/Test: tests/CodexQuotaTaskbar.Host.Tests/UI/TrayMenuPopupMotionTests.cs、IslandPointerRoutingTests.cs、IslandInteractionPreviewTests.cs；可新增职责单一 `TrayMenuFirstFrameTests.cs` 保存顺序/状态协议测试和离屏样本，先说明复用判断。
- Reuse/read: QuotaPopoverWindow.xaml.cs、OverlayCoordinator.cs/Bongo.cs、现有几何测试。不得为测试加入生产采集器/测试专用钩子或复制生产算法。
- Output: artifacts/reliability/menu-first-frame/{baseline,after} 的状态JSON、离屏对比PNG/必要视觉片段、TRX和命令日志；SDD task-1-report.md 写完整过程。

**Interfaces:**
- Consumes: 当前 PreparePopupOpen/SetPopupEntranceAnchor、ShowPopupAt/RepositionIslandMenu、IsVisibleChanged/Loaded、StartPendingPopupEntrance、Cancel/Hide/Refresh policy。
- Produces: 同一个打开generation从隐藏首态准备经Show到启动时钟；具体私有方法签名由实施者就现有职责最小拆分，不添加外部配置。保留现有内部 PlayPopupEntrance 调用契约或逐一说明必要调整。

- [ ] **Step 1: 写测试并采基线。** 先阅读本brief与DIAG report，核对baseline源码/产物SHA。新增有行为意义的测试：实际托管MenuShell在打开准备边界应已是动画首态；同代次后续准备/启动不能从完整显示再次归零；代次、取消、策略变更、四方向/DPI与不足空间结果。测试同协议分别记录基线与修复状态/布局，基线至少要证明原Prepare/锚准备阶段opacity1的顺序风险；若不能复现真正双弹就明确未复现，不造首帧。
- [ ] **Step 2: RED。** 在任何生产改动前构建运行最小新增回归测试，保存命令、退出码、TRX及失败断言；应因原首态尚未准备而失败，不能只有符号缺失/源码文本断言。基线可视样本来自实际生产表面状态，动画插值可用现有真实WPF AnimationClock离屏采样；标明采样/合成，不能伪造 IsVisible/IsLoaded 或宣称显示/失焦链已经实际跑过。先采用无Show/无HWND安全方案；如必要证据只能可见窗口完成，准确回报边界，不执行。
- [ ] **Step 3: 最小生产拆分。** 在隐藏时按SystemParameters.ClientAreaAnimation与ReduceCompanionMotion准备opacity/scale/offset；锚计算完成再Show。显示后仅排同一generation一次时钟，不再重置已可见表面。Loaded/Visibility/显式路径共用单一启动权威；取消/失败定位应释放pending，策略变更直接规范化；保留快速toggle、generation、捕获与失焦的既有语义。
- [ ] **Step 4: GREEN与隔离覆盖。** 运行新增用例及相关既有安全测试；覆盖初创建/复用状态、closing-toggle/后续新输入、旧回调/时钟中断、减少动画/系统禁动画/中途策略变化、DPI96/144/192/四向/空间不足。QuotaPopover→菜单和快速左右切换若仅托管路由/独立状态组合，明确非真实窗口切换；Dot路径分开写未覆盖部分。不得把隐藏模拟设置称真实焦点测试。
- [ ] **Step 5: 保存对比证据。** 同一协议、同一测试fixture比较基线/修复。实际状态/布局与时钟采样分列，能解释“修复前1、准备后0、可见后无再次归零”的证据来源与限制。不只方法次数。无实际可见复现时结论只能“修正候选顺序”。
- [ ] **Step 6: 自审并冻结。** Release构建与安全过滤测试完成，列精确命令/总数/Pass/Fail/Skip/退出码。不跑无筛选全集：本仓库有真实账号/网络/原生窗口/工作台和沙盒副作用用例；使用已有安全过滤边界并记录排除项。自审代码及测试，确认无正式配置写入/产品启动/输入。保存完整报告与准确改动文件列表；只提交本人明确生产/测试文件，中文功能提交，不暂存根的计划/文档，不推送。

## 根收尾与评审

- 根先冻结源码archive、现有Host/Core/TestDLL与受保护状态，独立预审本方案后派一个新实施者；任务级规格/质量审查问题必须闭合。
- 根复核Release与测试结果，必要时一次适当安全验证；对e8d2117..HEAD整次试修做最终审查（同一小diff可合并任务/最终审查，但必须明确覆盖完整范围）。
- 新本地包用独立构建输出及build-only身份，不改VERSION，清楚区别已安装 local0.1.17/e8d；优先重用旧Inno6.4.1经SHA/有效签名/版本核验的既有编译器，不下载/部署新工具。仅ZIP/Setup产物，不安装启动。
- 根更新原集中验收报告、中文修改日志及本任务材料，保护复核，按明确文件本地提交。计划/证据与原成果保留；不执行工作树/分支清理。
