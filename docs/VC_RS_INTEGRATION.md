# vc-rs 集成基线

FoxVoice 固定使用以下上游源码版本：

```text
repository: https://github.com/shirohata/vc-rs.git
commit: 2c3b57661c4a38f56e64c521a71e65dc68db5895
package: vc-core 0.5.0
license: MIT
```

当前已直接复用 `vc-core::model_rvc::inspect_model` 作为 ONNX 导入门禁。该检查器读取 ONNX
protobuf 元数据和输入输出契约，不需要创建 ORT Session，因此可以在复制模型文件前拒绝损坏或
不兼容的模型。

`foxvoice-engine` 直接复用 `vc-app::EngineController` 和 `RealtimeConfig`。Windows 发布构建启用
`windowsml`，默认选择 `WindowsMlDirectMl`；输入回调、输出回调、重采样、RVC worker、SOLA
拼接、实时线程优先级及遥测均由固定的上游实现负责，FoxVoice 只提供进程协议与产品配置。

后续实时引擎使用同一固定版本的 `RvcPipeline`、`ChunkConverter`、DSP 与 SOLA/PSOLA 实现。
WindowsML、CUDA 和原生 TensorRT 继续按独立进程/独立组件构建，禁止把不同发行包中的
`onnxruntime.dll` 混入同一目录。

MIT 只覆盖 `vc-rs` 源码。ContentVec、RMVPE、RVC 权重和用户模型必须分别保存来源、许可证
与 SHA-256；FoxVoice 不把任何外部权重声明为 MIT。
