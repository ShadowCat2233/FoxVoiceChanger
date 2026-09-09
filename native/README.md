# FoxVoice Native

FoxVoice 的 Windows 原生监督层。当前阶段负责：

- 硬件和系统能力探测；
- 推理后端推荐；
- 组件清单与隔离规则校验；
- 游戏模式性能状态机；
- 为后续 `vc-core`、WASAPI 和桌面 UI 提供稳定协议。

## 开发环境

在项目根目录运行：

```powershell
.\scripts\bootstrap-native.ps1
```

默认使用 Windows 正式开发所需的 MSVC 工具链。只进行纯 Rust 逻辑验证时可以使用：

```powershell
.\scripts\bootstrap-native.ps1 -Toolchain gnu
```

终端用户不需要安装 Rust。正式安装包只分发编译完成并经过校验的二进制组件。

## 命令

```powershell
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- doctor
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- recommend
cargo run --manifest-path native/Cargo.toml -p foxvoice-supervisor -- validate-components native/config/components.json
```

## vc-rs 上游基线

计划接入的上游基线固定为：

```text
repository: https://github.com/shirohata/vc-rs.git
commit: 2c3b57661c4a38f56e64c521a71e65dc68db5895
```

在完成 API、许可证和实时性能审计前，不直接将上游 `main` 作为可变构建依赖。
