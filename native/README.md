# FoxVoice Native

FoxVoice 的 Windows 原生监督层。当前阶段负责：

- 硬件和系统能力探测；
- 推理后端推荐；
- 组件清单与隔离规则校验；
- 游戏模式性能状态机；
- 有大小上限的长度前缀 JSON IPC 帧；
- 实时线程可用的固定容量无锁 SPSC 音频缓冲；
- 可选 WASAPI 设备枚举与无模型安全旁路。

## 开发环境

在项目根目录运行：

```powershell
.\scripts\bootstrap-native.ps1
```

默认使用 Windows 正式开发所需的 MSVC 工具链；缺少 Visual Studio Build Tools 时会从
微软官网下载、验证发布者签名并自动安装 C++ 工作负载。若只想检查环境而不允许安装：

```powershell
.\scripts\bootstrap-native.ps1 -SkipBuildToolsInstall
```

只进行纯 Rust 逻辑验证时可以使用：

```powershell
.\scripts\bootstrap-native.ps1 -Toolchain gnu
```

终端用户不需要安装 Rust。正式安装包只分发编译完成并经过校验的二进制组件。

## 命令

```powershell
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- doctor
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- recommend
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- validate-components native/config/components.json
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- ipc-demo
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- audio-buffer-demo
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- models list
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- models import C:\path\voice.onnx
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- models huggingface https://huggingface.co/owner/repo/resolve/main/voice.onnx
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- models huggingface-files https://huggingface.co/owner/repo
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- foundation-models status
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- foundation-models install --accept-gpl
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- models recycle model-0123456789abcdef
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- models convert model-0123456789abcdef
cargo run --manifest-path native/Cargo.toml -p foxvoice-engine --features windowsml -- validate-rvc --model voice.onnx --embedder contentvec.onnx --f0 rmvpe.onnx
cargo run --manifest-path native/Cargo.toml -p foxvoice-engine --features windowsml -- play-wav --file effect.wav --output "CABLE Input" --gain-db -3
```

模型库默认位于 `%LOCALAPPDATA%\FoxVoice\models`。测试和便携运行可通过
`FOXVOICE_DATA_DIR` 改写数据根目录。导入使用 SHA-256 内容 ID 去重并原子提交；删除只移动到
`.recycle`，不会立即永久删除。

Hugging Face 仓库浏览只返回 `.onnx`、`.pth` 和 `.index`。下载中断时保留按 URL 稳定命名的
临时文件，下次使用 `Range` 续传；服务器不接受续传时自动从头覆盖。代理优先读取
`FOXVOICE_PROXY`，其次读取标准代理环境变量，最后仅在端口连通时探测 `127.0.0.1:7897/7890`。

`.pth` 转换在独立的 `foxvoice-converter` 进程中完成，当前上游转换器只支持启用 F0 的
RVC v2 检查点。输出采用 vc-rs 的流式 ONNX 契约，随后再次经过模型库结构校验和 SHA-256
去重；转换器失败不会终止桌面控制服务。

WASAPI 使用可选特性，防止纯 Rust 测试机被 Windows SDK/链接器阻塞。正式的
Windows x64 构建应在已安装 Visual Studio Build Tools 的 MSVC 环境执行：

```powershell
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor --features wasapi -- audio-devices
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor --features wasapi -- bypass-test 2
```

GNU 工具链只用于协议、状态机和缓冲算法的快速验证，不作为发布工具链。

## vc-rs 上游基线

计划接入的上游基线固定为：

```text
repository: https://github.com/shirohata/vc-rs.git
commit: 2c3b57661c4a38f56e64c521a71e65dc68db5895
```

在完成 API、许可证和实时性能审计前，不直接将上游 `main` 作为可变构建依赖。
RVC 基础模型默认位于 `%LOCALAPPDATA%\FoxVoice\components\rvc-foundation`。安装命令只在
显式传入 `--accept-gpl` 后执行，并校验上游公布的文件大小和 SHA-256；权重不进入发布包。
