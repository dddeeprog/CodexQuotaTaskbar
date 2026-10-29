# 无工作台面板分支 Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development. Steps use checkbox syntax for tracking.

**Goal:** 在独立分支停用新增工作台入口，保留源码、实验和正常点岛修复。

**Architecture:** 从核实的9841701创建App托管工作树，以审核过的差异补丁带入24个正常修复文件。取消工作台启动接入，隐藏按钮并切断事件；保留工作台源码和设置兼容字段。

**Tech Stack:** 现有 .NET 10 / C# / WPF / xUnit；依赖仅复用本机缓存。

**Spec:** 本会话已审阅方案与老大明确回复“授权上述核查与隔离实施”；交接 PH-20261029-pointisland4-handoff-01 仅作历史事实来源。

## Global Constraints

- 原目录文件、索引及未跟踪工作保留；不切换原main、不stash/reset/clean。
- 不推送、安装、启动真实候选、重写旧提交、联网测试、物理输入或访问调试端口。
- 不执行历史被拒File.Move/原子持久化，包括会间接触发它们的回归测试。
- 新独立功能使用中文本地提交；正常修复迁移与关闭入口分别审阅。
- 工作台/Composer实验在原目录保留；本轮不继续研究。

## Review Focus

- 旧配置含工作台目录/快捷键时仍正常加载，设置不丢失。
- 仅隐藏菜单不足以阻止全局快捷键；必须取消启动注册。
- 隐藏按钮不得留下菜单空行或参与键盘导航，合成Click不得转发工作台事件。
- 更新共享状态必须连同App接入迁移，菜单与自动检查不能重复安装。
- 悬停说明的清理、Dot/拖动抑制、球环几何与正常截图恢复保留。

## Task 1：保全并迁移正常修复

- [x] 保存原33项及8项实验和2份交接文件哈希、索引哈希。
- [x] 审核24个源/测试文件的完整差异并通过git apply --check后仅在隔离工作树应用。
- [x] 记录逐文件差异片段、功能归属；混合日志不迁移，实验/旧报告原位保留。
- [x] 缓存依赖离线restore/build，运行白名单基线；真实输入/网络/File.Move测试不执行。
- [x] 将更新防重入、小球和悬停修复、回归资产分别中文本地提交。

## Task 2：关闭工作台接入

**Files:** App.xaml.cs；UI/TrayMenuWindow.xaml及.xaml.cs；Tray/TrayController.cs；tests/UI/CompactTrayMenuTests.cs；README.md。

- [x] 先添加菜单隐藏/禁用/无转发及启动不注册工作台的回归，确认旧实现红测。
- [x] 移除StartWorkbench启动调用；按钮Collapsed/Disabled；断开菜单Click与托盘转发。事件声明、App.Workbench.cs、StopWorkbenchAsync及配置字段保留。
- [x] 运行对应绿测及正常功能离线白名单、构建、diff检查。
- [x] 独立复核无可达工作台入口及正常修复保留；中文提交关闭功能与说明。

## Task 3：验收交付

- [x] 再核原43文件、原索引、main/原改动清单；核对新分支和提交。
- [x] 在现有修改日志追加本轮隔离工作树记录，保留原目录日志。
- [x] 保存迁移清单、红绿测试与构建证据，说明通过/失败/未测及恢复步骤。
- [x] 保留App托管工作树供验收；未合并，不归档或删除。恢复面板只在该分支撤回关闭提交，原main一直保留。

## 已审核迁移片段（相对9841701）

全部通过补丁适用性校验后应用，不覆盖原目录。源码差异中没有工作台新增依赖。下面的范围为新侧片段起点及长度；完整补丁保留在artifacts/no-workbench-panel/normal-fixes.patch。

| 文件 | 新侧片段 | 归属 |
|---|---|---|
| src/CodexQuotaTaskbar.Host/App.xaml.cs | 21; 330,2 | update |
| src/CodexQuotaTaskbar.Host/UI/CompanionBallControl.cs | 28; 51,3; 68,9; 142; 149; 229; 231 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/CompanionBallVisual.cs | 54; 62,5; 198; 201,4; 271; 414,2; 420; 422 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/CompanionPoseEngine.cs | 110; 148; 178,8 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.Bongo.cs | 23 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.Companion.cs | 11,2; 23; 66; 84; 98,2; 156,7 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.DotInline.cs | 210; 238 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.Input.cs | 95,0; 99,4; 111; 155; 165 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.Schedule.cs | 10,2; 17,5; 30; 71,73; 145; 148,14; 171,2; 181 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.xaml.cs | 76,3; 92 | companion-ui |
| src/CodexQuotaTaskbar.Host/UI/TrayMenuWindow.Companion.cs | 37,10 | companion-ui |
| src/CodexQuotaTaskbar.Host/Update/UpdateWorkflow.cs | 10,2; 13,2; 21,5; 39,10; 58,4; 66,13 | update |
| tests/CodexQuotaTaskbar.Host.Tests/Installer/InstallerSetupTests.cs | 90,14 | regression-assets |
| tests/CodexQuotaTaskbar.Host.Tests/Resets/QuotaResetTrackerTests.cs | 609,17 | regression-assets |
| tests/CodexQuotaTaskbar.Host.Tests/UI/CompanionCarouselTests.cs | 110,77 | companion-ui |
| tests/CodexQuotaTaskbar.Host.Tests/UI/CompanionResetFloatTests.cs | 11,2; 19; 124,6; 147,130 | companion-ui |
| tests/CodexQuotaTaskbar.Host.Tests/UI/CompanionRgbRingTests.cs | 5,2; 13; 37; 48,128 | companion-ui |
| tests/CodexQuotaTaskbar.Host.Tests/UI/DotNativeClickRuntimeTests.cs | 10; 24,9; 37; 39,10; 65,2; 70,10; 86,2; 125,14; 153,12; 166; 235; 248,2 | regression-assets |
| tests/CodexQuotaTaskbar.Host.Tests/UI/IslandPointerRoutingTests.cs | 42,6; 197,27 | companion-ui |
| tests/CodexQuotaTaskbar.Host.Tests/UI/ResetScheduleRowTests.cs | 3; 381,351 | companion-ui |
| tests/CodexQuotaTaskbar.Host.Tests/UI/TrayMenuWindowTests.cs | 198,2 | companion-ui |
| tests/CodexQuotaTaskbar.Host.Tests/Update/GitHubUpdateServiceTests.cs | 8,9; 21,16; 111,6 | update |
| tests/CodexQuotaTaskbar.Host.Tests/Update/UpdateApplierTests.cs | 36,21 | update |
| tests/CodexQuotaTaskbar.Host.Tests/Update/UpdateWorkflowTests.cs | 95,65 | update |


原日志990行混合追加不迁移；原始TRX、原生外部输入fixture、Composer识别/绑定实验与脚本继续留在原目录，不当作新候选通过证据。

## 实际交付与验证

- 工作树：C:/Users/TomatoK/.codex/worktrees/no-workbench-panel/CodexQuotaTaskbar（App托管）；分支codex/no-workbench-panel。原拟议同级目录未创建，使用App原生生命周期管理。
- 24文件迁移提交：84e7e87更新防重复，7e70422小球/UI，8052524回归资产。它们保留既有修复，不把原混合日志或识别实验混提。
- 本轮关闭接入：4个生产文件与现有CompactTrayMenuTests；App.Workbench.cs、Workbench目录、AppSettings字段相对9841701无改动。
- 缓存源离线restore成功；Release整个解决方案构建0警告0错误。
- 迁移基线213/213；菜单红测45中5个预期失败；关闭后菜单45/45；主agent最终离线回归409/409。各批次有重叠，不相加。
- 独立代码审查无P1/P2问题；菜单用未Show真实控件与合成事件，启动和Tray接线用编译IL及源码路径核对。没有启动实际App证明运行进程状态。
- 原33项清单、43个文件哈希及原索引均核对一致，原main仍为9841701。
- 未运行全量测试：排除真实鼠标/快捷键、网络、设置持久化、更新文件替换、工作台Git/文件操作及媒体导出入口；不把它们记为通过。
- 原目录所有工作和未跟踪资源保留；本轮不删文件、不归档仍待验收的工作树。artifacts/no-workbench-panel下补丁、迁移表、哈希、TRX、构建和审查证据保留供验收，合并且无需复验后再按App机制保全证据并归档。

### 复核命令与恢复

缓存依赖：`dotnet restore CodexQuotaTaskbar.slnx --source C:/Users/TomatoK/.nuget/packages --ignore-failed-sources -p:NuGetAudit=false`。

构建：`dotnet build CodexQuotaTaskbar.slnx -c Release --no-restore`。测试使用Host.Tests的FullyQualifiedName白名单，完整命令和边界记录在artifacts/no-workbench-panel/delivery.json。运行前只在测试子进程清除CQT_*和CODEX_QUOTA_*，不改用户配置。

回退不触碰原目录：原main继续保留原工作台和全部原改动；如需在无面板分支恢复工作台，只撤回本轮“停用工作台接入并保留源码与正常功能”提交，前三个正常修复迁移提交继续保留。未合并、未推送、未安装；不执行reset/clean/stash。
