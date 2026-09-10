# FoxVoice 实施状态

更新时间：2026-09-10

## 已完成

- 可操作的产品工作台原型；
- 修正版产品与技术架构；
- Rust workspace 与跨进程协议基础类型；
- Windows 图形适配器与驱动版本探测；
- WindowsML、TensorRT、CUDA、CPU 后端推荐规则；
- 推理组件分目录和冲突组校验；
- 游戏模式六级降级状态机；
- 1 MiB 上限、长度前缀 JSON IPC 帧与控制命令契约；
- 固定容量无锁 SPSC 音频环形缓冲，欠载补静音、过载丢弃并计数；
- WASAPI 设备枚举与无模型安全旁路实现（可选 `wasapi` 特性）；
- 自动安装并验证微软签名的 Visual Studio Build Tools、Windows SDK 与 MSVC Rust 工具链；
- 本机 WASAPI 设备枚举和 2 秒真实音频流旁路自检；
- 本地 RVC 模型库：`.onnx`、`.pth`、`.index` 分类导入、SHA-256 去重、原子提交、清单列表和可恢复删除；
- 固定 `vc-rs` commit，并直接复用 `vc-core` 的 ONNX protobuf/RVC 输入输出结构检查器；
- Windows WPF 桌面工作台，连接真实硬件诊断、音频设备、模型库、旁路进程和实时指标；
- `win-x64` 自包含发布流程，终端用户无需安装 .NET、Rust 或 Visual Studio；
- 独立 WindowsML/DirectML 推理进程，直接复用 `vc-app` 的音频线程、重采样、固定队列、RVC worker 和遥测；
- 桌面端可从模型库选择 Generator，并指定 ContentVec/RMVPE 后启动完整三模型 RVC 管线；
- Hugging Face 官方域名 `resolve` URL 导入，包含 HTTPS、文件类型、4 GiB 上限、临时文件清理和模型门禁；
- 桌面设置持久化，所选输入/输出设备会传递给独立引擎；
- 推理进程异常退出时，桌面控制层刷新设备并自动启动默认设备安全旁路；
- `doctor`、`recommend`、`validate-components`、`guard-demo`、`ipc-demo` 和 `audio-buffer-demo` 命令；
- Rust 开发依赖下载与 SHA-256 校验脚本；
- 13 个原生层单元测试、格式检查和 Clippy 零警告。

## 本机自检结果

- 操作系统：Windows x64；
- 显卡：NVIDIA GeForce RTX 4060 Ti；
- 其他适配器：GameViewer Virtual Display Adapter；
- 当前推荐：WindowsML；
- 原因：TensorRT 组件尚未安装和通过自检；
- `.index`：游戏实时模式默认不参与推理；
- 组件清单：4 个组件，schema v1 校验通过。
- 音频输入：HECATE G2 GAMING HEADSET 麦克风，48 kHz 单声道；
- 默认输出：FxSound Speakers，48 kHz 双声道；
- 2 秒旁路：0 输入过载、0 流错误；两次运行观察到 192–576 个输出采样欠载（约 4–12 ms）。
- 正式 `vc-app` 旁路持续运行：80+ 块、0 输入过载、约 20 个启动欠载采样，双时钟缓冲稳定。

## MVP 后续增强

1. 下载进度、取消与断点续传；
2. `.pth` 隔离转换进程；
3. 音效板与全局热键；
4. TensorRT 独立组件和压力测试；
5. 本地训练与离线编辑器。

此阶段不会加入训练、FAISS 检索或自研虚拟驱动，先保证音频闭环和故障恢复稳定。

## 当前验证边界

本机已使用 MSVC 编译完整 `wasapi`/`windowsml` 特性，20 个测试、格式检查和 Clippy 均通过，并完成真实
设备枚举与短时旁路。当前仍是共享模式原型：尚未实现设备热插拔恢复、跨进程共享内存、时钟
漂移补偿和 RVC 推理，因此不能视为游戏场景的最终低延迟验收。
