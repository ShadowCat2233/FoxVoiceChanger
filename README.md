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
- 组件中心可按需安装 ContentVec 与 RMVPE：下载前展示 GPL-3.0，下载后强制校验文件长度与 SHA-256，并自动写入 RVC 路径；
- 检测 VB-CABLE 并链接到官方安装说明；主输出选择虚拟声卡后，可用独立低优先级进程监听到物理耳机，监听故障不终止游戏语音主链路；
- 游戏保护根据每秒真实处理耗时、输出欠载和流错误自动切换 160/240/320 ms 配置，持续超载时进入保声旁路，稳定后逐级恢复；前台全屏/无边框应用检测还会预先切稳定档并暂停新后台重任务；
- 设置页提供不占用音频设备的 RVC 三模型自检：在 WindowsML/DirectML 上加载 Generator、ContentVec、RMVPE 并执行一帧合成，报告加载和推理耗时；
- Hugging Face 输入框同时接受单文件 `resolve` 地址和仓库/分支地址；仓库会列出可导入文件供选择，下载支持稳定临时文件与 HTTP Range 续传，并自动探测显式环境代理或本机 7897/7890 代理；
- 空闲时每 10 秒刷新 WASAPI 设备；运行中设备丢失由引擎错误隔离触发重新枚举，并恢复到当前默认设备的安全旁路；
- 音效板支持最多 24 个 PCM/Float WAV、独立增益、重采样、当前主输出共享混音以及前 8 个音效的系统级 `Ctrl+Alt+F1…F8` 热键；每次播放使用低优先级隔离进程；
- 模型库可把 PCM/Float WAV 送入同一 RVC 三模型管线离线转换，支持音高设置、单声道下混、重采样和临时文件提交；转换时不会占用麦克风或实时音频设备；
- 独立 `foxvoice-engine` 进程复用 `vc-app`/`vc-core` 的 RVC、SOLA、重采样和双时钟音频运行时；
- Windows ML bootstrapper 与再分发许可证经过固定 NuGet SHA-256 后进入发布包和单文件内嵌运行时；NVIDIA RTX 30 系及以上可通过系统 EP 目录按需安装 TensorRT RTX，并以当前三模型真实推理通过后启用；
- RVC 进程异常退出后刷新设备并自动回退到默认设备安全旁路；
- 自动安装开发依赖，生成无需 .NET/Rust 的自包含 Windows 发布目录。
- 模型训练页可按需安装固定版本的官方 RVC 训练源代码、Python 3.12、FFmpeg、隔离 venv、CPU/CUDA PyTorch 和训练基础权重；安装可取消，检测到游戏时自动停止并允许稍后续装。
- 训练环境就绪后可由 FoxVoice 启动/停止本机官方训练工作台；生成的 `.pth` 与 `.index` 可批量经过 FoxVoice 哈希、去重和格式门禁导入模型库。

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

基础模型可在“组件中心”由用户明确确认后从上游直接下载。FoxVoice 不重新分发这些权重；
取消许可确认、下载不完整或哈希不匹配都会终止安装，原文件不会被覆盖。
