# 兼容性探针使用说明

这个包只用于收集本机 Windows 与任务栏结构证据。`--collect-only` 是只读模式：不会注入、不会改动 Explorer、不会创建自启动项，也不会联网。

解压后，在包目录中运行：

```powershell
.\CodexQuotaTaskbar.CompatibilityProbe.exe --collect-only --output .\collect-only.json
```

输出 JSON 只包含 Windows 版本、架构、模块摘要、任务栏类型摘要与判定结果；不会记录用户名、完整用户路径、窗口标题或命令行。`ProbeRequired` 表示尚未完成兼容性验证，这是 collect-only 的正常结果。`Unsupported` 表示当前结构不在已知安全范围内。

若报告为 `Unsafe`，请停止后续操作，不要尝试 `--live`。保留 JSON 与包的 SHA-256 文件，并运行同目录的恢复请求脚本：

```powershell
powershell -NoProfile -File .\recover-probe.ps1
```

恢复脚本只会向匹配且已记录的探针发送软停止请求；它不会启动、终止或重启 Explorer。若仍有异常，请重启电脑后再把报告交给开发者分析。
