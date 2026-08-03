# Codex Quota Taskbar

一个贴着 Windows 任务栏显示的 Codex 额度小岛。它使用中性的 iOS 风格毛玻璃外观，不注入 `Explorer.exe`，也不修改任务栏 XAML，因此可以独立运行，并能与 Windhawk 共存。

## 功能

- 在任务栏边缘显示 5 小时与 7 天剩余额度。
- 点击小岛打开详情，区分 `Codex`、`Spark` 等额度来源，并显示当前订阅计划。
- 订阅徽标使用额度档位名称：`PRO 5X`、`PRO 20X` 等。
- 按住小岛拖动即可自由摆放；拖动位置在本次运行期间保持不变，重新启动后恢复任务栏贴边位置。
- 使用 `BlurredBackground.WPF` 呈现透明毛玻璃，不包含蓝色光影。
- 支持多显示器、不同 DPI、深浅色、高对比度、全屏隐藏和任务栏自动隐藏。
- 托盘和发布包统一使用原创“双额度轨道”图标；右键菜单采用与小岛一致的中性液态玻璃样式。
- 托盘菜单提供立即刷新、检查更新、打开 Codex、显示范围、提醒、自动更新、开机启动和退出。
- 默认连接本项目 GitHub 正式 Release 自动更新，也可从托盘手动检查或关闭自动更新。
- 不需要 Windhawk；安装了 Windhawk 时也无需关闭或修改它。

## 数据说明

应用通过本机已登录的 Codex App Server 只读获取额度。仅安装 Codex 桌面版时，应用会将桌面包内的官方 Codex 可执行文件复制到自己的本地运行缓存后启动，从而避开 Windows 商店目录禁止外部启动的限制，并继续使用现有 ChatGPT 登录：

- 小岛上方固定显示“5 小时”，下方固定显示“7 天”。
- 如果账号当前没有单独的 5 小时窗口，上方会暂时复制 7 天额度，避免显示空白；详情页仍展示服务端返回的真实窗口。
- 普通 Codex 额度标记为 `Codex`，`codex_bengalfox` 周额度标记为 `Spark`。
- 订阅计划只读取账号响应中的 `planType`；应用不会读取或显示邮箱。
- `prolite` 显示为 `PRO 5X`，`pro` 显示为 `PRO 20X`；这两档对应当前 Codex Pro 的 5 倍与 20 倍额度方案。
- 无法刷新时会保留最近一次数据并标记为陈旧，过期后显示不可用。
- 启动时立即刷新；服务端通知后约 300 毫秒刷新，并且每 3 分钟自动兜底刷新一次。

## 直接使用

系统要求：Windows 10/11 x64，以及已经使用 ChatGPT 登录的 Codex 桌面应用或 Codex CLI。便携包已内置 .NET 10 Desktop Runtime，新电脑无需另行安装 .NET。

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

## 项目结构

- `src/CodexQuotaTaskbar.Core`：额度模型、解析与投影逻辑。
- `src/CodexQuotaTaskbar.Host`：正式 WPF 浮层、详情卡、托盘和 Codex 数据源。
- `src/native`、`tools/CodexQuotaTaskbar.CompatibilityProbe`：保留的兼容性诊断与验证工具，不进入正式 Host 发布包。
- `tests`：单元测试、原生测试与安全边界检查。
- `docs/使用说明.md`：更详细的日常使用说明。

## 第三方组件

本项目采用 [GNU General Public License v3.0](LICENSE) 开源。界面模糊效果使用 [BlurredBackground.WPF](https://github.com/NullTale/BlurredBackground.WPF)，完整第三方声明见 `THIRD_PARTY_NOTICES.md`。
