# 点岛可靠性 1→5 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 v0.1.16 上顺序补齐更新、额度、提示、诊断和性能证据；只交付本地可审阅提交。

**Architecture:** 复用更新服务、额度 tracker、自动页脚及 typed 诊断。以可注入的文件/进程边界隔离更新事务，业务状态用确定性序列测试，日志只接受白名单记录。性能先采同机同场景基线，再优化可测热点。

**Tech Stack:** C#/.NET 10 x64，WPF，xUnit，现有 PowerShell 构建；不增加依赖。

**Spec:** 本文“约束和完成定义”完整承载 PI-RELIABILITY-005 v1 授权；任务原始证据和资源归属在 artifacts/reliability/protection-before.json，原五项优先级不得互换。

## Global Constraints

- 源码基线 a75012623b77830190eb7162a3c24828dc8b52f0，专用分支 codex/reliability；正式 release 工作树只读。
- 只本地开发、构建、隔离 fixture/模拟进程、独立审查及中文提交；不推送、发布、安装或替换正式程序。
- 不操作正式退出、真实账号/登录、ACL/防护/提权；自动更新保持关闭。旧 main 43 文件/index/状态及各分支、回退包/证据保留。
- 安装失败历史 0x80070005 不能推断成特定对象、ACL 或防护原因；23b 仅诊断成果，49 项旧测试不能冒充回滚验收。
- 不把合成预览、离屏控件、模拟 DPI/多屏/RDP 写成生产实机验收。

## Review Focus

- 覆盖成功后启动失败与不确定启动：旧版不能被丢弃，未证实新版退出前不能覆盖正在运行的目标。
- 备份/替换间断电及重复入口：持久事务与哈希对账先于重试，残留 ready 不能证明本次成功。
- 旧异步失败与账号切换交错：旧 generation 不能覆盖新状态或触发绿。
- 提示暂时无空间/隐藏再恢复：不应消费未实际展示的事件；已有绿 1 小时不因重绘延长。
- 日志中异常正文/路径及磁盘错误：白名单、容量上限和主异常保持，导出取消不产生文件。

## 约束和完成定义

每项先调查、给方案和测试边界，先 RED 再 GREEN，独立评审后中文功能提交，再进入下一业务项。共享同一工作树时实现只由当项负责人写，其他 agent 只读。项目暂无适用的既有可靠性计划；新增此文件作为五项统一方案，artifacts/reliability 只存本任务证据。

### Task 1: PI-UPDATE-REL-001 更新事务

**Files:** `Update/UpdateApplier.cs`, `Update/UpdateApplyOptions.cs`, `Update/UpdateStatus.cs`, `Update/GitHubUpdateService.cs`, `Update/GitHubReleaseParser.cs`, `App.xaml.cs`；必要的新文件 `Update/UpdateTransaction.cs`, `Update/UpdateStartup.cs`, `Update/UpdatePackageIdentity.cs`；对应 `tests/.../Update/*Tests.cs`。

**Interfaces:** 复用 GitHubRelease、UpdateLaunchState、UpdateApplyDiagnostic；新增 identity 验证负责正式 URI/tag/摘要、PE x64+产品/文件版本；transaction receipt 绑定本次 nonce、目标及新旧 SHA、版本/阶段，startup 确认绑定事务和实际进程版本。实现时以真实类型签名记录于任务 ledger，不另建重复更新服务。

- [x] 调查现有摘要/ZIP 单文件/256MB 下载和160MB EXE限额，复用 23b 阶段诊断；保留历史失败根因未知结论。
- [x] 新增行为测试并观察失败：EXE版本错配/x86/坏PE/非正式URI；父退出超时/取消；复制与替换拒绝、磁盘不足；启动 null/异常/立即退出/超时/旧 ready；启动失败恢复、恢复失败、清理叠加错误；中断后对账及重复进入。
- [x] 实现：暂存源在任意卷先复制至目标同目录，经再次哈希校验后做单 EXE 同卷原子替换并保留旧版；阶段日志持久化，等待匹配新版就绪后提交。失败只有确认新进程已退出才回退；无法确认则保留备份并明确待恢复状态。恢复前校验新旧哈希，拒绝覆盖外部变化。单文件原子性不等于多文件/整流程原子，跨卷源仅复制，不能承诺多文件原子。
- [x] 兼容冻结 v16 的严格三参 `--apply-update PID target`：新 helper 独立按自己的实际版本从固定官方 tag 获取元数据、验证归档并比对自身 EXE。旧父没有传摘要或事务凭据，缺字段不得默认可信。自证网络失败不替换；旧父已退出才可尝试正常恢复启动，父仍活着则禁止重复启动。隔离测试只提供假 HTTP/生命周期，不连真实服务或正式目录。
- [x] 隔离文件与模拟生命周期测试通过；不运行旧测试中未授权真实 File.Move 路径，不启动正式目录。取消/超时不能抹掉主异常，恢复/清理错误作为附加诊断。
- [x] 独立评审与中文提交；完成定义：可验证包进入事务、就绪成功才完成、可恢复失败旧版可用、恢复失败可定位且备份不丢、当前实际版本准确，所有未测边界清楚。

### Task 2: PI-QUOTA-STATE-002 过期异步状态

**Files:** `Provider/CodexRateLimitProvider.cs`，现有 tracker/client/provider 测试。

**Interfaces:** 继续使用 tracker 的实际成功证据/freshness/scope和预告锁定；provider 的所有发布路径共用同一 auth generation guard。

- [x] 盘点成功/失败/缓存、账户 scope 与额度轮次；新增延迟旧请求失败、invalid account、切换通知后的旧回包测试，先观察失败。遵守真实 refreshLock 顺序：旧 A 返回后 B 才能开始刷新，不能构造不可能的并行 B 成功来夸大覆盖范围。
- [x] 最小补齐 generation gate，不把预计到点转为绿，不让旧 A 失败覆盖切换通知后的新账户状态或破坏 tracker；随后 B 首样本仅建基线、第二次实际回升才绿。
- [x] 复用 tracker/latch 回归，并补过期缓存/轮次与错误边界；独立审查、中文提交。
- [x] 完成定义：只有实际新鲜成功回升进入绿，过期回包不覆盖新状态；无法获得可靠账户身份时保守降级并注明边界。

### Task 3: PI-POPUP-REL-003 提示显示恢复

**Files:** `UI/QuotaCapsuleWindow.Schedule.cs`, `UI/QuotaCapsuleWindow.xaml.cs`, `UI/CompanionBallControl.cs`, `UI/CompanionBallVisual.cs`及对应现有测试；以调查结果决定最小修改。

**Interfaces:** 消费既有自动提示事件、同一 latch 和布局空间；保持红绿单行/金两行、业务优先级及绿1小时。

- [x] 盘点实际显示与事件消费顺序，编写窄屏/遮挡收回/恢复/Dot共存/状态切换的确定性失败序列。
- [x] 仅修复可证实缺口；用可用空间/显示条件驱动恢复，不依赖 hover，不重播同一已展示事件。
- [x] 区分视觉展示与业务消费：实际额度回升仍应消费旧预告，即便窗口隐藏；不能为等视觉展示而使旧公告在绿到期后复活。
- [x] 运行布局边界与三色回归，产出带环境/DPI标记合成预览；独立审查、中文提交。
- [x] 完成定义：可展示事件不会被隐藏错误消费，同一事件展示期正确且不重播；物理多屏/DPI未测明确列出。

### Task 4: PI-DIAG-EXPORT-004 脱敏诊断导出

**Files:** 复用 `UI/CompanionDiagnostics.cs`, `UI/OverlayLifecycleDiagnostics.cs`；新增 `Diagnostics/ReliabilityDiagnostics.cs`和 `Diagnostics/DiagnosticExport.cs`；`Tray/TrayController.cs`及 App 入口，相关测试。

**Interfaces:** typed事件只接受版本、阶段/状态/抑制原因的枚举与有限数值；外部异常仅归类和HResult；导出由用户主动选本地路径，默认无上传。

- [x] 先测试注入敏感正文/原始路径、坏日志、超限、取消/导出错误与轮转，观察失败。
- [x] 正常可管理的受管文件总量和单文件有界；导出只重新序列化允许字段，不复制原日志目录；版本与状态/抑制原因可关联。
- [x] 接入用户菜单与本地选择器，取消无副作用，导出失败给安全错误；独立审查、中文提交。
- [x] 完成定义：可定位为什么失败/为什么抑制且敏感样本不出包，默认不外传。

### Task 5: PI-PERF-DISPLAY-005 性能与显示证据

**Files:** 隔离基准放现有测试项目的专用工具/测试入口；候选热点 `UI/CompanionDiagnostics.cs`，仅在测量支持后修改；证据在 artifacts/reliability/perf。

**Interfaces:** 同一 scenario、机器、预热、持续时间和采样方式；记录版本SHA、CPU/内存/分配/写入次数；不向正式进程注入、不重启正式实例。

- [ ] 固定合成状态变化及动画场景，采基线；另列 DPI/多屏/RDP 模拟测试和未测实机项。
- [ ] 用失败测试刻画候选热点（如同步磁盘写入阻塞生产调用），实现最小优化，保持日志有界与关闭时安全刷新。
- [ ] 相同场景采优化后结果，不用单线程分配冒充整机内存/CPU；解释噪声与不成立的结论。
- [ ] 独立审查、中文提交，完成定义：前后可重复证据、无行为回归，未做实机 RDP/多屏不称通过。

## 最终交付

- [ ] 汇总每项SHA、实际测试数量/失败/跳过/未测、独立审查及风险；补中文修改日志。
- [ ] 重新核保护快照，清理明确可再生且不再用的本任务 fixture；源码/分支、原成果、回退和测试证据保留。
- [ ] 无自动合并、推送、发布或安装。资源仍待审阅，保留专用工作树。
