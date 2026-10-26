# Dot 网页桥接验证工具

仅用于本机可行性和内存验证，不属于安装包，不在主解决方案的发行链路中。

## 范围

- 复用已有 `Microsoft.Web.WebView2` 版本与 `BrowserSubscriptionProtocol` 导航边界。
- 使用额度岛自己的 `%LOCALAPPDATA%/CodexQuotaTaskbar/WebView2` 登录资料；运行前须关闭其他使用该资料目录的订阅窗口。不会复制 Edge/Codex Cookie、导出令牌、刷新 Codex 登录或绕过网页验证。
- 可见加载官方 `https://chatgpt.com/dots/home`；登录、验证码由用户手动完成，第三方登录弹窗暂未适配。
- DOM 桥接仅接受精确官方来源和 Dot 路径，以旧测试回复 `DOT-OK-4827` 确认是本次获授权的会话；不是通用用户身份校验方案，不可直接用于正式版。
- 每次运行最多发送一条无工具操作的测试提示。请求使用随机测试标记，回复须完整匹配才显示在原生 WPF 区域；不把既有草稿、任意历史或访问凭据导出给原生程序。无任何提交重试；结果不明时也不重发。
- 两次开关周期：空壳基线约8秒 → 打开并验证 → 回复后采样12秒 → Dispose并观察30秒 → 重新打开只读验证 → 再关闭30秒 → 验证进程退出。登录/回复等待各有90秒上限。

## 运行

```powershell
dotnet build tools/CodexQuotaTaskbar.DotBridgeProbe/CodexQuotaTaskbar.DotBridgeProbe.csproj -c Release -o artifacts/dot-bridge-probe/bin
node --test tests/dot-web-bridge.test.mjs
& ./artifacts/dot-bridge-probe/bin/CodexQuotaTaskbar.DotBridgeProbe.exe (Resolve-Path ./artifacts/dot-bridge-probe).Path
```

运行命令会打开可见网页，并在确认已知会话后发送测试消息。不要在无测试授权的账号上运行。

## 内存口径

- JSONL 位于 `artifacts/dot-bridge-probe/measurement-<PID>.jsonl`，CreateNew 不覆盖旧记录。
- 每2秒统计验证宿主及其 WebView2 后代；含 crashpad 等浏览器辅助进程。
- 后代归属必须满足父子启动时间顺序，观察过的 PID 另以启动时间校验，避免 Windows PID 重用造成误归属；不统计现有额度岛、其他浏览器或 Codex。
- 主口径为私有字节总和，辅助记录工作集总和；后者可能重复计算共享页，不等于物理 RAM 净增量。
- 同时记录 Dispose 与 BrowserProcessExited 时间。不会强制垃圾回收、裁剪工作集或杀进程制造“释放”结果。
- 这是隔离原型，不是已集成的额度岛。独立空壳本身占用不能直接当成正式版新增开销；关闭网页后宿主保留的程序集/缓存与浏览器残留应分开判断。
- 完整网页仍会加载官方 UI 及资源；隐藏网页或把聊天文本画在原生面板上，不等于这些资源自动消失。

## 证据保留

原始采样保留用于复核；构建缓存可在验证结束且无进程占用时重新生成。初次运行 `measurement-57404.jsonl` 存在父 PID 重用导致的误归属，不能直接用其中合计列作最终结果。之后的修正版运行才作为正式验证口径。结果未发布，用户安装版不受影响。

## 2026-10-23 实测

正式采用 `artifacts/dot-bridge-probe/measurement-38852.jsonl`（WebView2 155.0.4283.62）。同一验证进程内完成两次打开/关闭，首次发送后约16.18秒观察到精确回复 `DOT-WV-f34b5c7d`；第二次仅重新读取该回复，没有重发。

| 阶段 | 验证宿主＋浏览器私有内存 | 其中浏览器 | 浏览器进程 |
| --- | ---: | ---: | ---: |
| 打开前，末3个基线样本 | 81.24–85.80 MiB | 0 | 0 |
| 首次回复后，末5个样本 | 734.71–918.52 MiB | 645.68–826.84 MiB | 8 |
| 首次关闭后，末5个样本 | 88.13–89.36 MiB | 0 | 0 |
| 第二次加载后，末5个样本 | 876.78–931.17 MiB | 786.43–841.05 MiB | 7–8 |
| 第二次关闭后，末5个样本 | 89.91–98.46 MiB | 0 | 0 |

全程私有内存采样峰值937.88 MiB。两次 Dispose 到 BrowserProcessExited（Normal）分别约0.27/0.28秒；随后每次继续观察30秒，最后验证宿主正常退出。未强杀、未主动GC，宿主关闭前保留的少量运行时/程序集开销不能归为浏览器进程未释放。

已有额度岛 PID4640 未重启，结束时私有163.59 MiB、工作集311.07 MiB；安装文件SHA256仍为 `8CBE802CAD0E302A9223C32ADFE081AF727D7DC986031CDF7B73CAD045762991`。这不是集成版本A/B测试，也不是长期泄漏或其他设备的性能保证。

结论：现有登录资料可用于本机网页桥接；原生发送→官方网页→Dot回复→原生显示的最小路径通过，但完整官方页面的内存较高，不推荐常驻。若后续实施，优先考虑独立按需聊天进程、关闭整个桥接进程；这会牺牲关闭后的主动消息监听，不能同时声称“零后台开销”和“实时主动通知”。白色小球及32表情仍应保持原生绘制。
