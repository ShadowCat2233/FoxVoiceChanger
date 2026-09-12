# 狐声 FoxVoice

一款面向 Windows 11 的本地实时 RVC 变声器。FoxVoice 使用 WPF 构建桌面界面，使用 Rust
运行音频、模型管理和推理任务；声音与模型默认留在本机，不依赖云端推理服务。

> 当前版本：`0.3.0`。项目仍处于早期开发阶段，请先在非关键语音场景中测试设备路由和模型效果。

## 主要功能

- WASAPI 实时麦克风输入和虚拟声卡输出；
- 标准 RVC v2 F0 ONNX 实时推理，支持 ContentVec、RMVPE 和 Generator 三模型自检；
- 本地及 Hugging Face 模型导入、SHA-256 去重、授权确认和可恢复删除；
- 每模型独立保存音高、索引混合率、清辅音保护和 F0 平滑设置；
- `.index` 特征检索、离线 WAV 转换和音效板混音；
- DirectML/Windows ML 后端，以及自检通过后启用的可选 TensorRT RTX 后端；
- 游戏保护：监测处理预算、欠载和流错误，超载时优先保持语音链路连续；
- 隔离的 RVC v2 训练环境、一键训练、阶段进度和训练结果导入；
- 自包含便携版和当前用户安装器，支持升级、修复、回滚与保留模型卸载。

## 快速开始

1. 从 [Releases](https://github.com/ShadowCat2233/FoxVoiceChanger/releases) 下载安装器或便携版。
2. 安装 [VB-CABLE](https://vb-audio.com/Cable/) 或兼容虚拟音频设备。
3. FoxVoice 输入选择物理麦克风，主输出选择 `CABLE Input`。
4. KOOK、Discord 或游戏的麦克风选择 `CABLE Output`。
5. 在“组件中心”安装并自检 RVC 基础模型。
6. 导入你有权使用的标准 RVC v2 F0 模型，选择模型后启动变声。

```text
物理麦克风 → FoxVoice → CABLE Input → CABLE Output → KOOK / Discord / 游戏
                                      └→ 可选物理耳机监听
```

本地监听默认应关闭。需要监听时请选择独立物理耳机，避免重复监听、回声或扬声器啸叫。

## 模型兼容性

当前主要支持标准 RVC v2 F0 模型。PyTorch `.pth` 检查点需要先转换为兼容 ONNX；模型结构、
训练数据和音高范围都会影响最终效果。严重失真、辅音破碎或异常音色不一定能通过参数调节修复。

FoxVoice 不附带第三方人物模型。请只使用自行训练、获得明确授权或许可证允许使用的模型与声音素材。
ContentVec、RMVPE、训练权重和其他外部资产也分别受各自许可证约束。

## 模型训练

训练页可在 `%LOCALAPPDATA%\FoxVoice\training` 安装隔离的 RVC 训练环境，支持 CPU 或 NVIDIA
CUDA 后端。选择包含 WAV/FLAC 的授权数据集后，程序会执行数据清洗、特征提取、模型训练和索引生成。
训练任务可停止，已有检查点会保留。

训练会消耗大量磁盘、CPU/GPU 和时间。训练期间不要运行游戏或实时变声；CPU 训练仅适合验证流程，
正式训练推荐使用兼容的 NVIDIA GPU。

## 构建

需要 Windows 11 x64、.NET 8 SDK、Rust MSVC 工具链和 Visual Studio C++ Build Tools：

```powershell
.\scripts\bootstrap-native.ps1
.\scripts\build-release.ps1
```

发布产物位于 `artifacts/`：便携目录、便携压缩包、当前用户安装器及对应 SHA-256 校验文件。

## 项目结构

- `desktop/`：.NET 8 WPF 桌面应用；
- `native/`：Rust 音频引擎、模型层、转换器和 supervisor；
- `setup/`：安装、修复、升级、回滚和卸载程序；
- `scripts/`：环境引导、测试和发布脚本；
- `docs/`：架构、实施状态与上游集成说明；
- `app/`：早期产品界面原型，仅供设计参考。

## 当前限制

- 尚未接入 Seed-VC 独立高质量引擎；
- 不保证所有社区 RVC 模型都能兼容或获得理想音质；
- 尚未完成覆盖所有游戏、KOOK/Discord 版本和 GPU 满载场景的系统性验收；
- TensorRT 仅在依赖、硬件和三模型真实推理自检全部通过后启用；
- Windows 原生环境不提供 AMD ROCm 训练后端。

## 隐私与安全

实时音频、模型和训练数据默认只在本机处理。设置保存在 `%LOCALAPPDATA%\FoxVoice`。Hugging Face
下载会在导入前进行地址、体积、哈希和模型结构检查。提交问题时请移除用户名、绝对路径、模型哈希及
无权公开的音频或模型。

## 参与贡献

欢迎通过 Issue 报告问题或提出建议。请附上 FoxVoice 版本、Windows 版本、CPU/GPU、推理后端、
模型类型、复现步骤和已脱敏的诊断信息。提交代码前请确保构建通过，并保留第三方许可证和来源声明。

## 开源许可

FoxVoice 自有代码采用 [MIT License](LICENSE) 开源。第三方组件及可选下载内容不自动适用本项目许可，
详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
