# Codex Quota Taskbar

一个贴着 Windows 任务栏显示的 Codex 额度小岛。它使用中性的 iOS 风格磨砂外观，不注入 `Explorer.exe`，也不修改任务栏 XAML，因此可以独立运行，并能与 Windhawk 共存。

## 功能

- 在任务栏边缘显示 5 小时与 7 天剩余额度。
- 岛内第三行显示最近一次公共重置的时间与类型，数据来自 [Codex Resets](https://codex-resets.com/zh-CN)；点击详情可查看最近三条中文公告预览，不显示 X 推文入口或正文链接。
- 额度小岛始终显示额度；检测到 Codex 会话运行、等待输入、受阻或完成时，会在小岛旁显示独立的会话胶囊。
- 会话胶囊固定显示在额度岛下方；收起时用错位叠层表现多个任务，展开后最多同时露出三个，更多任务可在胶囊区使用鼠标滚轮查看；点击任务可直接回到对应的 Codex 会话。
- 额度岛右上角使用 macOS 三点式玻璃状态灯：红色表示待授权、黄色表示进行中、绿色表示刚完成，数字显示在各自圆心并在增减时平滑过渡；内部 `subagent` 子会话不会显示，也不会计入角标。
- 点击小岛打开详情，区分 `Codex`、`Spark` 等额度来源，并显示当前订阅计划、订阅有效期、额外点数与公共重置公告；详情会避开下方的会话胶囊。
- 订阅徽标使用额度档位名称：`PRO 5X`、`PRO 20X` 等。
- 按住小岛拖动即可自由摆放；拖动位置在本次运行期间保持不变，重新启动后恢复任务栏贴边位置。
- 额度岛、详情、会话和右键菜单使用统一的中性磨砂表面。右键菜单的“材质透明度”支持 0%–100%，默认 0% 不透底；拖动即时同步所有浮层并自动保存，不改变文字和图标透明度。保留 `BlurredBackground.WPF` 背景层、柔和描边、阴影和动画，不包含蓝色光影。
- 支持多显示器、不同 DPI、深浅色、高对比度、全屏隐藏和任务栏自动隐藏。
- 托盘和发布包统一使用原创“双额度轨道”图标；右键菜单采用与小岛一致的中性磨砂样式，操作文字与会话标题使用相同的字体、字号和字重。
- 托盘菜单提供立即刷新、检查更新、打开 Codex、材质透明度、显示范围、提醒、订阅有效期、自动更新、开机启动和退出。
- 默认连接本项目 GitHub 正式 Release 自动更新，也可从托盘手动检查或关闭自动更新。
- 不需要 Windhawk；安装了 Windhawk 时也无需关闭或修改它。

## 数据说明

应用通过本机已登录的 Codex App Server 只读获取额度。仅安装 Codex 桌面版时，应用会将桌面包内的官方 Codex 可执行文件复制到自己的本地运行缓存后启动，从而避开 Windows 商店目录禁止外部启动的限制，并继续使用现有 ChatGPT 登录：

- 小岛上方固定显示“5 小时”，下方固定显示“7 天”。
- 如果账号当前没有单独的 5 小时窗口，上方会暂时复制 7 天额度，避免显示空白；详情页仍展示服务端返回的真实窗口。
- 普通 Codex 额度标记为 `Codex`，`codex_bengalfox` 周额度标记为 `Spark`。
- 订阅计划只读取账号响应中的 `planType`，额外点数读取官方 App Server 额度响应中的 `credits.balance`；应用不会读取或显示邮箱。
- “订阅有效至”优先使用同一账号已保存的网页登录结果；没有时读取本地 Codex 登录信息中的订阅时间，缺失或已过期时再通过固定的 ChatGPT HTTPS 订阅接口只读补全。详情显示来源、更新时间；失败不影响额度刷新，旧结果标记“缓存”。
- 服务拒绝查询时，可点击详情中的“网页登录 · 更新订阅日期”或托盘“网页登录订阅”。在可见的官方页面手动登录、完成验证后，点击“已登录，读取订阅”。必须与 Codex 登录相同账号；Google 等第三方登录可能限制内嵌浏览器，服务也可能不提供日期。
- “订阅有效期”默认开启，可在托盘菜单关闭。原有本地读取不会写回登录凭据或主动刷新令牌；可见网页使用独立 WebView2 资料目录保留登录状态，不导入其他浏览器数据，也不把网页令牌回传应用。可从窗口中清除网页登录及网页日期缓存。完整读取范围见 [隐私与数据说明](docs/privacy.md)。
- 网页日期只在窗口中手动更新，超过 3 分钟标为缓存、到期弃用；普通额度仍按原有周期自动刷新。
- 订阅有效期表示服务端披露的当前订阅有效截止时间，不保证订阅会在当天取消；自动续费、账号切换或登录信息尚未更新都可能使它变化。它不是额度重置时间，私有接口变化或仅使用系统密钥环保存登录时也可能暂不可读。
- `prolite` 显示为 `PRO 5X`，`pro` 显示为 `PRO 20X`；这两档对应当前 Codex Pro 的 5 倍与 20 倍额度方案。
- 无法刷新时会保留最近一次数据并标记为陈旧，过期后显示不可用。
- 启动时立即刷新；服务端通知后约 300 毫秒刷新，并且每 3 分钟自动兜底刷新一次。
- 公共重置公告独立每 15 分钟读取一次，详情“刷新”或托盘“立即刷新”也会更新公告。中文来自网站自身译文，按公告编号、时间、类型及原文匹配；没有中文时会明确标注原文，不调用额外翻译服务。常规重置与备用重置明确区分；备用重置需在 Codex 中手动使用，公共公告不代表当前账号已经到账，也不预测下一次重置。断网时显示缓存或暂不可用，不影响原有额度读取。
- 会话状态每 2 秒核对一次。因为独立 App Server 无法读取另一个桌面进程的内存状态，应用只读监听 `.codex/sessions` 中最近修改的任务事件，并从 `.codex/session_index.jsonl` 读取 Codex 已生成的任务名称；不读取终端内容，也不上传会话正文。
- 会话显示优先级为：需要输入、任务受阻、可以查看、正在运行；应用启动前已经完成的历史任务不会突然显示为新任务。刚完成的任务保留 30 秒，从额度岛打开查看后立即移除；Codex 桌面端没有可供第三方读取的“已查看”回执，因此直接在 Codex 中查看时由 30 秒窗口自动清理。

## 直接使用

系统要求：Windows 10/11 x64，以及已经使用 ChatGPT 登录的 Codex 桌面应用或 Codex CLI。便携包已内置 .NET 10 Desktop Runtime，新电脑无需另行安装 .NET。

可选的网页登录窗口另外需要 Microsoft WebView2 Runtime；若缺失，窗口会提供微软官方下载入口，额度岛本身不受影响。

1. 解压 `CodexQuotaTaskbar-win-x64.zip`。
2. 双击 `CodexQuotaTaskbar.exe`。
3. 左键点击小岛查看详情；按住并拖动可调整位置；右键打开液态玻璃托盘菜单。详情会根据小岛当前所在的屏幕边缘自适应定位。

便携包是包含运行时的单文件自包含版本，不会自动写入开机启动。需要时可在托盘菜单中手动开启。

## 自动更新

- 启动约 15 秒后检查一次，此后每 6 小时检查一次；仅安装 GitHub 上标记为正式版且高于当前版本的 Release。
- 下载目标固定为 `dddeeprog/CodexQuotaTaskbar` 仓库中的 `CodexQuotaTaskbar-win-x64.zip`。
- 安装前同时校验 GitHub 返回的 SHA-256、ZIP 单文件结构和 Windows EXE 文件头；校验失败不会替换当前程序。
- 更新完成后小岛自动退出、原位替换并重新启动，已有设置与开机启动路径不变。
- 托盘菜单中的“自动更新”可关闭后台检查，“检查更新”可随时手动触发。

## 构建

源码构建使用仓库 `global.json` 中指定的 .NET SDK。PowerShell 中运行：

```powershell
.\build.ps1 -Target Build -Configuration Release
.\build.ps1 -Target Package -Configuration Release
```

生成的文件位于：

- 可运行目录：`artifacts/host-publish/Release/`
- 便携 ZIP：`artifacts/releases/CodexQuotaTaskbar-win-x64.zip`

运行无账号依赖的演示模式：

```powershell
.\artifacts\host-publish\Release\CodexQuotaTaskbar.exe --demo
```

## 验证

完整验证会执行托管测试、原生桥接测试、边界扫描和发布包检查；需要 Visual Studio C++ 构建工具与 CMake：

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

网页登录读取脚本另有不使用真实账号的 Node.js 测试：`node --test tests/browser-subscription-reader.test.mjs`。

## 项目结构

- `src/CodexQuotaTaskbar.Core`：额度模型、解析与投影逻辑。
- `src/CodexQuotaTaskbar.Host`：正式 WPF 浮层、详情卡、托盘和 Codex 数据源。
- `src/native`、`tools/CodexQuotaTaskbar.CompatibilityProbe`：保留的兼容性诊断与验证工具，不进入正式 Host 发布包。
- `tests`：单元测试、原生测试与安全边界检查。
- `docs/使用说明.md`：更详细的日常使用说明。

## 第三方组件

本项目采用 [GNU General Public License v3.0](LICENSE) 开源。界面模糊效果使用 [BlurredBackground.WPF](https://github.com/V4SS3UR/BlurredBackground.WPF)，完整第三方声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。项目许可证、第三方声明和字体 OFL 许可证均作为资源嵌入自包含的单文件程序。
