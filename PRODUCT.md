# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

主要用户是在 Windows 11 上使用 Codex、同时可能启用 Windhawk 任务栏模组的个人开发者。他们希望无需打开 Codex 窗口，就能持续看到当前账号的短期与长期额度。

## Product Purpose

在任务栏旁提供不抢焦点、可随时扫一眼的 Codex 剩余额度胶囊；点击后展示完整窗口、重置时间和数据状态。成功意味着显示准确、位置稳定、退出干净，并且完全不影响 Explorer 或 Windhawk。

## Positioning

产品通过独立桌面浮层贴近任务栏，而不是注入或修改 Explorer；额度只从本机 Codex 官方 App Server 获取。订阅有效期增强功能可读取本地登录信息并查询固定的 ChatGPT 接口，范围与限制见 `docs/privacy.md`。

## Operating Context

Windows 11 桌面、一个或多个显示器、混合 DPI、四边任务栏与自动隐藏场景。应用常驻通知区域，用户可刷新、选择显示范围、切换开机启动或退出。

## Capabilities and Constraints

- 正式应用不得注入 Explorer，不得依赖 BridgeControl、兼容性探针或原生注入桥。
- 必须与 Windhawk 共存，不自动修改 Windhawk 或系统无障碍设置。
- 通过 `codex app-server --listen stdio://` 获取当前登录账号的额度。
- 支持多屏、Per-Monitor-V2、真正全屏隐藏、会话锁定隐藏和 Explorer 重建后的重新定位。
- 胶囊默认全部任务栏显示，可切换为仅主任务栏。
- 设置不得持久化凭据或原始 App Server 消息。

## Brand Commitments

名称为 Codex 额度；视觉采用 iOS 控制中心取向的原生 WPF 磨砂表面：272 × 68 DIP 可见胶囊、右上角 macOS 三点式红黄绿会话状态角标、中性灰渐变、圆角方形额度图标、双额度条与公共重置摘要，以及银白/琥珀/红色的额度状态。岛、详情、会话和菜单材质统一，默认不透底，用户可在右键菜单调整 0%–100% 材质透明度并自动保存；文字与图标不被淡化。透明效果关闭或高对比度启用时仍回退为原生纯色表面，信息与操作保持可用。

## Evidence on Hand

- 已确认规格：`docs/superpowers/specs/2026-08-02-codex-quota-taskbar-overlay-design.md`
- 已有兼容性探针证明 Explorer/XAML 注入与当前 Windhawk 配置冲突；它只作为诊断证据，不进入正式产品。
- 官方 Codex App Server 文档提供账号与额度协议；没有需要伪造的商业或性能声明。

## Product Principles

- 一眼可读，操作时安静不打扰。
- 与系统和 Windhawk 共存优先于深度嵌入。
- 额度来源可验证，失败时诚实显示陈旧或不可用。
- 最小权限、凭据仅用于已披露的本机与官方订阅查询、退出不留后台资源；公共重置请求不携带账号或凭据。
- 多屏与缩放是正常环境，不是例外路径。

## Accessibility & Inclusion

支持高对比度、关闭透明效果与减少动画；状态不得只依赖颜色，详情卡支持键盘焦点和 Escape 关闭。
