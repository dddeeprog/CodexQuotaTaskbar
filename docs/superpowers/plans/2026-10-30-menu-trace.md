# 临时菜单诊断 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. User already authorizes implementation, independent review, local package/replacement/normal restart. No repeated general approval.

**Goal:** 给当前仍复现的菜单现象采最多3次、每次约600ms的固定元数据诊断；默认关闭，不改正常行为，诊断版本机安装启动后交用户自行复现。

**Architecture:** 菜单部分文件仅插入受保护的观察调用；独立typed session负责单调时序、预算/期限/容量和离线批量写入，WPF adapter负责临时Rendering与自身HWND钩子的attach/detach。正常关闭/动画/定位路径保持原样。

**Tech Stack:** C#/.NET10/WPF/xUnit/现有DiagnosticLogStore；复用既有官方便携Inno6.4.1。

**Spec:** PI-MENU-TRACE-011 v1授权；010只读证据在C:/Users/TomatoK/Documents/Codex/2026-10-29/task-3/pointisland-menu-repro-010。008已安装为v18，用户仍报告重复弹出，不能预设唯一根因。

## Global Constraints

- 唯一工作树 C:/Users/TomatoK/.codex/worktrees/reliability/CodexQuotaTaskbar，起点6d8714a5a297889a71ea44c9765dbab7d7dfe038（功能0ff1b2e）；原main25M8U/43/index、其他分支、旧代码/改动/五项成果/daily/workbench取消状态、全部旧包/配置保留。perf/fixtures曾拒删，不重试。
- 当前已装0.1.18+local.menu-first-frame.0ff1b2ed98bf751bdb5e6212ad73cdd7d5a9d642，EXE SHA2A00A8A6E3B0D3C0EEAC7758EEF735B05043A82229C75DB9205AAC9019325A59；fresh确认后备份本轮v18完整安装文件/六配置/现v18Setup，正常退出后再保存最新六配置，不能拿v17/v16备份代替。
- 不改任何正常交互/动画参数/关闭重入/布局/更新/登录；不新增协议、权限、安全设置、全局钩子或输入处理；不改模型/账号/防护。不推送/公开发布/tag，不访问被限制依赖源码、不下载屏摄/绕过Library失败。
- 默认不启用；仅此次进程环境变量CQT_MENU_TRACE_ONCE=1启用，不永久配置。整个进程（非每窗口）最多3次accepted隐藏菜单打开分支，拒绝/重复/visible/closing-toggle不消耗新预算；600ms单调时间硬截止，hide/dispose/异常更早结束，不自动重新武装。
- 每episode最多128记录，session总最多512，最终单个本地trace文件最多512KiB；固定枚举/布尔/数值，无无界字符串/集合。每帧仅内存buffer（禁止逐帧调用现OverlayLifecycleDiagnostics.Record，因为它200ms会写盘），episode结束后异步批量写，本地路径固定Diagnostics/menu-trace-<pid>.json，测试不能写正式目录；失败不能传播UI。
- JSON只留本地：无凭据/账号/正文/URL/键入/外部窗口标题/进程命令/屏幕图像/全局鼠标坐标。只采自身菜单几何；前景仅Menu/Details/Capsule/Other/None角色，不采外部身份。
- 不占生产鼠标、不注入输入、实施验证不显示影响工作区的窗口；原62安全回归+必要隔离样本，不全量扩大。正常退出若无可用接口，具体请用户手动退出，不能强杀。标准安装一次，失败不循环；启动一次，不代开菜单消耗3次。

## Review Focus

- 默认禁用无采样/文件/Rendering/native hook负担；允许诊断时观察不改变Handled、原返回值或正常调用顺序。
- 预算只在accepted新打开消耗；超时/隐藏/关闭/异常解绑，禁止后台轮询重新武装。
- 同一Stopwatch频率/时轴，pending消耗和真实Render-start/完成区分请求；Rendering不是DWM present。
- 固定类型、128/512/512KiB硬边界与写入失败隔离；无任意窗口文本或敏感字段。
- 当前v18恢复材料fresh；替换/正常启动仅一次、配置保持、启动后trace为Armed/remaining3，不自动触发菜单。

### Task 1: 临时typed trace、观察接线与隔离测试

**Files:**
- Create: src/CodexQuotaTaskbar.Host/UI/MenuTraceSession.cs（typed bounded session/serialization）、TrayMenuWindow.Trace.cs（WPF/native adapter）；若职责需要分文件先说明，避免过大混合文件。
- Modify only observation calls: TrayMenuWindow.xaml.cs、PopupMotion.cs、Placement.cs、Interaction.cs（Loaded/SizeChanged/material）、必要TrayController来源或现OverlayLifecycleDiagnostics的最小只读桥接。尽量复用现inputTimestamp与metadata，不能扩输入处理签名。
- Create/Test: tests/CodexQuotaTaskbar.Host.Tests/UI/MenuTraceSessionTests.cs、MenuTraceWindowTests.cs（后者只在必要时，复用STA helpers风格）。原62测试不改期望；不新增测试专用产品采集器/伪造正常可见性。
- Output: artifacts/reliability/menu-trace/tests，完整task-1-report.md写该计划SDD目录；单写者只提交自身明确源码/测试文件，中文功能提交，根稍后提交计划/日志。

**Interfaces / observation requirements:**
- 固定事件包括ShowRequest/Accepted/Rejected、ActualShowBefore/After、Activated/Deactivated、Activate/Focus返回值、Prepare/Play/PendingConsume/RenderStart/Complete/Cancel/Normalize、FrameSample、NativeShow/Activation/Position、PositionRequest/actual own bounds+DPI、SizeChanged/Loaded/MaterialEnable/BackgroundChange、EpisodeStop/SessionStop。
- 每条同一单调tick、generation、visible/loaded/preparing/closing、固定来源/角色；表面采MenuShell和MenuContent opacity/scale/offset。输入关联可在accepted时只读取已有生命周期最后序号/时间戳/来源，不复制无关RelatedPid或内容。
- 自身HwndSource hook只读不设置handled，返回0且不改变原调用；自身消息/矩形只在episode采。前景通过已知本App窗口句柄分类，不查标题、外部进程命令。
- deadline采样必须拒绝tick>=600ms；WPF解绑依UI线程可执行性，若线程被阻塞要说明实际解绑延后边界，不能声称UI阻塞时仍能精确卸订阅。超时自动停止，不依赖再来一帧；Hide/Closed/异常均清理，finally退订。

- [x] **Step 1 RED:** 先写真实session行为测试，覆盖默认关闭、3预算/拒绝不耗、600ms边界无后续帧仍结束、generation/重复callback、128/512记录和512KiB文件上限；允许诊断session使用正常可注入clock/writer便于确定性，不能复制生产模型/源码grep。保存生产实现前期望失败与命令/退出码，不把缺符号当全部RED证据。
- [x] **Step 2 implement:** typed模型与可失败写入隔离，WPF adapter按上述观察接线；零行为改变。用ProcessPath准确门禁保证普通测试默认不启用，显式session注入只在诊断本身测试所需，不增加正常逻辑test-only钩子。启用进程写一次Armed状态，至多3结束状态异步批量快照，用固定字节上限检查；不含真实数据、不上传。
- [x] **Step 3 isolatedGREEN:** 验证预算/超时自动stop/重复拒绝、hide/dispose/异常解绑、generation/旧callback、writer异常/容量及敏感schema；WPF测试可托管不Show，实测非侵扰timer/解绑若只能窗口可见则回报边界，不占鼠标。明确Rendering采样不是屏幕帧。原62安全集合复跑；子进程清理CQT_*及CODEX_QUOTA_UI_PROOFS，诊断测试显式构造隔离session而非意外环境武装生产。禁Native_submenu以及App/账号/bridge/沙盒入口。
- [x] **Step 4 freeze/report:** Release构建0警告0错误，报告精确命令、actual test计数/退出码、启用方式/路径/3次停止说明/未测边界/观察完整性。根提取中文日志diff行号前暂不提交；随后仅提交本人文件中文功能commit，不push。报告READY待提取行号，完成后DONE。

## 根收尾与本机部署

- 独立预审→一个新实施者→独立规格/代码/完整新增范围审查，缺陷发原实施者修，根不直接修源。根保护/备份/文档/包/安装；不并发改源或共享输出build。
- 根复核精确安全测试、源与包身份；build-only0.1.19+local.menu-trace.<功能SHA>（未占用才使用）、File/Assembly0.1.19.0，源码VERSION不改。离线缓存还原与隔离输出，重核复用Inno；Setup和ZIP都不混入夹具。
- 包完成且审查通过后，正常退出当前v18（无接口则明确手动退出），核0产品进程/无重复安装，fresh退出后六配置。标准当前用户安装一次/记录exit，配置比对后，以仅子进程环境CQT_MENU_TRACE_ONCE=1正常启动一次。
- 验路径/PE+实际运行版本/主进程唯一健康/自动更新false/traceArmed remaining3 episode0。绝不代开菜单；安装完成立刻交付启用路径、用户先左详情再右菜单最多3次的复现说明，等后续trace再分析，不能宣称已捕获根因。记忆/额外索引不阻塞此回报。
- 计划/中文修改日志与本地报告只提交明确文件，保留原代码/改动及本轮回退/测试/构建证据；无清理，无项目推送。


执行补充：MenuToggle位于分支前，不能据它计budget；空间不足若已经进入accepted隐藏打开分支，算一次accepted尝试但以PlacementFailed结束并标记未Show，不算成功可见。Window重建共享本进程剩余预算。采样不得EnsureHandle/Show/Activate或修改capture/Handled/动画值；诊断异常只停止自己，原业务异常照旧传播。启动后要落enabled/armed/accepted0/samples0等价状态，禁止代开新菜单验版本。
