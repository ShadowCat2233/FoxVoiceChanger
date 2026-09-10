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

## 下一实施切片

1. 在 MSVC + Visual Studio Build Tools 环境编译并实机验证 WASAPI 旁路；
2. 将进程内无锁缓冲升级为 Audio Router 与 Engine Process 的共享内存数据面；
3. 接入音频设备变化通知、自动重连与默认设备迁移；
4. 固定 `vc-rs` 上游 commit 并实现 WindowsML 适配层；
5. 把 `doctor`、音频设备和实时指标接入桌面工作台。

此阶段不会加入训练、FAISS 检索或自研虚拟驱动，先保证音频闭环和故障恢复稳定。

## 当前验证边界

本机 GNU 工具链已验证全部纯 Rust 协议、缓冲、硬件探测和降级逻辑。由于本机尚未安装
Visual Studio Build Tools，依赖 Windows SDK 链接器的 `wasapi` 特性尚未完成编译和实机音频
验证；发布构建必须使用 MSVC，不能把 GNU 验证结果视为 WASAPI 已验收。
