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
- `doctor`、`recommend`、`validate-components` 和 `guard-demo` 命令；
- Rust 开发依赖下载与 SHA-256 校验脚本；
- 8 个原生层单元测试、格式检查和 Clippy 零警告。

## 本机自检结果

- 操作系统：Windows x64；
- 显卡：NVIDIA GeForce RTX 4060 Ti；
- 其他适配器：GameViewer Virtual Display Adapter；
- 当前推荐：WindowsML；
- 原因：TensorRT 组件尚未安装和通过自检；
- `.index`：游戏实时模式默认不参与推理；
- 组件清单：4 个组件，schema v1 校验通过。

## 下一实施切片

1. 定义 Audio Router 与 Engine Process 的 IPC 帧协议；
2. 加入固定容量共享内存环形缓冲区；
3. 接入 WASAPI 设备枚举与设备变化通知；
4. 实现无模型安全旁路，先打通麦克风到输出设备；
5. 固定 `vc-rs` 上游 commit 并实现 WindowsML 适配层；
6. 把 `doctor` 输出接入桌面工作台。

此阶段不会加入训练、FAISS 检索或自研虚拟驱动，先保证音频闭环和故障恢复稳定。
