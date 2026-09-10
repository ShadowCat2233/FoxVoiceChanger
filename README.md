# 狐声 FoxVoice

面向 Windows 11 x64 的本地实时 RVC 变声器。仓库包含 Windows 桌面程序、Rust 原生音频
监督层、模型库、硬件/推理后端检测以及产品工作台站点。

## 当前能力

- WASAPI 麦克风与输出设备枚举；
- 独立原生进程运行安全旁路并持续报告音频指标；
- `.onnx`、`.pth`、`.index` 模型导入、SHA-256 去重和可恢复删除；
- Hugging Face `resolve` URL 流式下载、域名/HTTPS/体积限制和下载后结构校验；
- 使用固定 `vc-core` commit 验证 RVC ONNX 输入输出结构；
- 硬件后端推荐与游戏压力降级状态机；
- WPF 桌面工作台：诊断、设备、模型库、旁路启停和实时指标；
- 独立 `foxvoice-engine` 进程复用 `vc-app`/`vc-core` 的 RVC、SOLA、重采样和双时钟音频运行时；
- RVC 进程异常退出后刷新设备并自动回退到默认设备安全旁路；
- 自动安装开发依赖，生成无需 .NET/Rust 的自包含 Windows 发布目录。

桌面端会把 ContentVec、RMVPE 和音频设备选择保存在
`%LOCALAPPDATA%\FoxVoice\settings.json`。模型文件保存在相邻的 `models` 目录。

## 构建

```powershell
.\scripts\bootstrap-native.ps1
.\scripts\build-release.ps1
```

成品位于 `artifacts\FoxVoice-win-x64`，运行 `FoxVoice.exe`。发布脚本同时生成可直接分享的
`FoxVoice-win-x64.zip` 和对应 `.sha256` 文件。

## 目录

- `desktop/`：Windows WPF 桌面应用；
- `native/`：Rust 音频、IPC、模型与 supervisor；
- `app/`：交互式产品工作台原型；
- `docs/`：架构、实施状态和上游集成边界；
- `scripts/`：依赖引导与发布构建。

实时推理需要用户合法取得的 RVC Generator、ContentVec 和 RMVPE 模型。外部权重不随 MIT
源码自动授权，模型来源、许可证与哈希必须分别记录。
