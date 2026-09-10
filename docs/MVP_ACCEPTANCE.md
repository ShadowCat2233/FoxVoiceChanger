# FoxVoice 本地 MVP 验收

验收日期：2026-09-10  
平台：Windows 11 x64，NVIDIA GeForce RTX 4060 Ti

| 范围 | 实现证据 | 验证结果 |
| --- | --- | --- |
| 桌面程序 | `desktop/FoxVoice.Desktop` | Release 构建 0 警告、0 错误；自包含程序启动后持续运行 |
| 音频路由 | `foxvoice-engine passthrough` | HECATE G2 输入到 FxSound 输出，48 kHz，80+ 块，0 输入过载 |
| 模型管理 | `foxvoice-models` | 本地/Hugging Face 导入、SHA-256 去重、原子提交、列表、解析、回收区 |
| RVC 接入 | `foxvoice-engine rvc` | 固定 `vc-app`/`vc-core` commit，WindowsML 构建通过，三模型参数进入 `RealtimeConfig` |
| 自动依赖 | `scripts/bootstrap-native.ps1` | 自动安装并验证 Rust、MSVC Build Tools 与 Windows SDK；完整脚本实机通过 |
| 发布构建 | `scripts/build-release.ps1` | 自包含目录、便携 ZIP、文件清单与 ZIP SHA-256 均实测通过 |
| 安全与恢复 | 模型门禁、下载限制、独立进程、旁路恢复 | 单元测试、Clippy 和运行时旁路验证通过 |

## 自动验证命令

```powershell
.\scripts\bootstrap-native.ps1
dotnet build .\desktop\FoxVoice.Desktop\FoxVoice.Desktop.csproj -c Release
.\scripts\build-release.ps1
```

Rust 工作区在 `wasapi,windowsml` 特性组合下共有 20 个测试通过，Clippy 使用 `-D warnings`
通过。发布清单中的 `FoxVoice.exe`、`foxvoice-supervisor.exe`、`foxvoice-engine.exe` 哈希均已
重新计算验证；便携 ZIP 的外部 `.sha256` 同样验证通过。

## MVP 边界

RVC 声音效果依赖用户提供合法的 Generator、ContentVec 和 RMVPE 权重，因此仓库不能凭空完成
某一声音的音质验收。本 MVP 已验证模型结构门禁、WindowsML 编译、正式引擎设备链路和三模型
加载入口；具体模型仍需在目标机器上执行听感、P95/P99 延迟及长时间游戏压力测试。

`.pth` 转 ONNX、训练、音效板、FAISS 实时检索、自研虚拟驱动和 TensorRT 是后续扩展，不属于
本地 MVP 完成条件。
