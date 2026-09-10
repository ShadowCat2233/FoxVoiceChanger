# 狐声 FoxVoice 产品与技术方案

状态：修正版 v0.3
目标平台：Windows 11 x64  
目标用户：个人与朋友小范围使用  
产品定位：面向游戏和语音聊天的本地实时变声器，同时提供模型管理、音效板、离线转换与本地训练能力。

## 1. 核心原则

1. 实时语音优先于界面动画、模型下载、音效板和训练任务。
2. 推理全部在本地完成，不上传麦克风音频、模型或训练素材。
3. 首版不重写完整 RVC 推理链，优先复用和适配 `shirohata/vc-rs` 的 `vc-core` 架构。
4. 推理后端以独立组件分发，每个引擎使用独立目录和进程，运行时只激活一个。
5. WindowsML/DirectML 是默认兼容路径，原生 TensorRT 是 NVIDIA 高性能路径。
6. `.index` 可以导入和保存，但首版游戏实时模式默认不执行检索。
7. 安装器和组件管理器负责依赖探测、下载、校验、安装、修复与回滚。
8. 已发布的交互原型和仓库中的 `app/page.tsx` 是桌面端的信息架构与视觉基准；未经明确确认，实施阶段不得改成另一套导航、布局或产品语言。

## 2. 已修正的技术结论

### 2.1 RVC 引擎

采用 `vc-core` 的设计和可复用代码作为推理基础：

- Rust 原生实时音频路径；
- ContentVec、RMVPE、RVC Generator 三段式 ONNX 推理；
- 滚动上下文、重采样、RMS/包络处理；
- SOLA/PSOLA 分块拼接；
- 锁自由音频回调与独立推理工作线程；
- WindowsML、ORT CUDA 和原生 TensorRT 后端抽象。

复用前必须执行许可证、依赖、模型格式和性能审计。FoxVoice 固定已验证的上游 commit，并维护自己的补丁和回归测试，不直接追踪上游 `main`。

`vc-rs` 的 MIT 许可证只覆盖其代码。ContentVec、RMVPE、RVC 模型等外部权重必须分别记录来源、许可证和哈希；GPL 标记的权重不得被误标为 MIT 内容。

### 2.2 GPU 推理后端

修正后的结论不是“CUDA 和 DirectML 在底层绝对不能共存”，而是：

- 官方预编译包可能包含不同构建版本的同名 `onnxruntime.dll`，不能跨包拼装 DLL；
- CUDA、DirectML、TensorRT 的驱动和运行库依赖不同；
- 为降低 DLL 搜索、ABI、升级和崩溃风险，FoxVoice 主动采用分组件、分目录、分进程隔离。

计划分发三个推理组件：

| 组件 | 默认状态 | 适用硬件 | 用途 |
| --- | --- | --- | --- |
| `fox-engine-windowsml` | 默认安装 | AMD、Intel、NVIDIA、核显 | DirectML 通用推理与 CPU 回退 |
| `fox-engine-tensorrt` | 按需安装 | NVIDIA RTX/GTX | 最低延迟和最高吞吐 |
| `fox-engine-cuda` | 后续可选 | NVIDIA | TensorRT 不兼容模型的回退路径 |

组件管理器根据系统、GPU 型号、显存、驱动版本和实测自检结果推荐后端，而不是只按显卡厂商判断。

### 2.3 AMD 与 ROCm

推理和训练分开处理：

- AMD 实时 ONNX 推理默认使用 DirectML；
- ONNX Runtime 1.23 起不再提供 ROCm EP，不能将其作为 FoxVoice 的 AMD 推理路径；
- 部分较新的 AMD GPU 已支持 Windows 原生 PyTorch ROCm，但支持范围受官方硬件矩阵限制；
- 支持矩阵内的 AMD 设备可以启用“实验性 Windows ROCm 训练”；
- 不在矩阵内的 AMD 设备使用 WSL2/Linux 训练，或选择 CPU 低速训练；
- NVIDIA 训练使用 Windows 原生 CUDA。

训练模块必须在启动任务前运行真实的 PyTorch、GPU、显存和算子自检，不能仅凭设备名称显示“支持”。

### 2.4 FAISS `.index`

FAISS 没有官方 Rust 绑定，但存在第三方 `faiss-rs`。因此“只能自行暴力检索或关闭”并不准确。

FoxVoice 首版策略：

- 模型管理器允许导入 `.index`；
- 记录文件路径、大小、哈希和推测索引类型；
- 游戏实时模式默认 `indexRate = 0`；
- 界面明确显示“索引已保存但当前未参与实时推理”；
- 不在实时音频线程中执行 FAISS 或暴力最近邻搜索；
- 后续将检索实现为独立可选组件，经过音质盲测和 P95/P99 延迟测试后再决定是否开放。

“开启 index 固定增加 500% CPU”没有足够依据，不作为产品设计参数。只把它视为可能明显增加检索耗时和尾延迟的风险。

## 3. 总体架构

```text
FoxVoice UI
    |
    +-- Control Service
    |     +-- 配置与预设
    |     +-- 模型管理
    |     +-- 组件管理
    |     +-- 性能与故障恢复
    |
    +-- Audio Router
    |     +-- WASAPI 输入
    |     +-- 监听输出
    |     +-- 虚拟麦克风输出
    |     +-- 音效板混音
    |
    +-- Realtime Engine Process
    |     +-- DSP 前处理
    |     +-- ContentVec
    |     +-- RMVPE
    |     +-- RVC Generator
    |     +-- SOLA/PSOLA
    |     +-- DSP 后处理
    |
    +-- Optional Worker Processes
          +-- 模型转换
          +-- Hugging Face 下载
          +-- RVC 训练
          +-- FAISS 检索（后续）
```

UI、训练、下载和推理必须分进程。UI 崩溃或下载卡住时，实时引擎应继续输出；推理引擎崩溃时，Audio Router 在一个缓冲周期内切换到安全模式。

## 4. 实时音频链路

```text
物理麦克风
  -> WASAPI 独占或共享输入
  -> DC/高通、门限、降噪、输入增益
  -> 滚动上下文与 16 kHz 重采样
  -> ContentVec + RMVPE
  -> RVC Generator
  -> RMS/包络、音色效果、限制器
  -> SOLA/PSOLA 拼接
  -> 音效板混音
  -> 虚拟麦克风
  -> Discord / 游戏语音
```

约束：

- 音频回调禁止锁、磁盘 I/O、网络 I/O、模型加载和动态内存分配；
- 推理只在工作线程或独立引擎进程中执行；
- 预分配环形缓冲区；
- 所有队列有固定容量和明确的溢出策略；
- 推理超时不阻塞音频设备回调；
- 监听默认关闭，避免啸叫和额外路由混乱。

## 5. 游戏模式与无边框窗口

无边框窗口本身不需要特殊音频实现。风险来自游戏与变声器争夺 GPU、CPU、内存带宽和调度时间。

游戏模式包含：

1. 实时引擎使用独立进程和高优先级音频工作线程，但不使用可能造成系统失去响应的实时进程优先级。
2. UI 自动降到 30 FPS，窗口失焦或最小化后暂停非必要动画和频谱绘制。
3. 推理预留安全余量：P99 推理时间必须低于音频块预算的 70%。
4. NVIDIA 可选择 TensorRT FP16，并限制 TensorRT workspace 和显存缓存上限。
5. DirectML 使用明确的 GPU 适配器，不因窗口焦点改变而重新选择设备。
6. 训练、模型转换、校验和下载在检测到游戏运行时自动暂停或限速。
7. 持续监控 underrun、overrun、推理时间、GPU 显存和设备重置。
8. 发生连续超时时逐级降级，而不是直接停止输出。

降级顺序：

```text
降低可视化刷新率
  -> 关闭非必要后处理
  -> 降低索引率至 0
  -> 增大推理块和输出缓冲
  -> 切换轻量模型/CPU F0
  -> 暂时旁路原声
```

旁路原声必须经过限幅并显示明显状态，避免引擎故障时向语音频道输出爆音或持续噪声。

## 6. 模型与 Hugging Face 管理

模型库支持：

- 本地拖放导入 `.pth`、`.onnx`、`.index` 和模型压缩包；
- Hugging Face 仓库 URL 或 `repo_id` 导入；
- 只下载所需文件，支持断点续传、代理、取消和重试；
- 下载前显示仓库许可证、文件列表和预计磁盘占用；
- 下载后执行 SHA-256、ONNX 结构、输入输出维度和恶意压缩包路径检查；
- 自动识别 RVC v1/v2、采样率、F0 模型、说话人数量和推荐后端；
- `.pth` 在隔离的转换进程中转为 ONNX，转换失败不影响实时引擎；
- 保存封面、作者、来源 URL、许可证、标签、测试结果和最后使用时间；
- 模型删除进入可恢复回收区，组件缓存和用户模型分开管理。

Hugging Face 适配不等于任意仓库都能直接运行。只有通过结构检查的 RVC 模型才能进入“可使用”状态。

## 7. 音效板与离线编辑

音效板：

- 支持 WAV、FLAC、MP3、OGG；
- 热键、音量、淡入淡出、循环和分组；
- 经过独立总线与变声结果混合；
- 独立限制器防止叠加削波；
- 游戏失焦时使用全局热键触发。

离线编辑首版采用单轨：

- 导入音频；
- 选择模型和预设；
- 波形选择、试听、裁剪；
- 导出 WAV/FLAC；
- 使用与实时模式相同的核心管线，保证调音结果具有可比性。

## 8. 自动安装与组件管理

主安装器只安装 FoxVoice Shell、通用 WindowsML 引擎和组件管理器。大型组件按需下载。

组件清单包含：

- 组件 ID 和版本；
- 支持的系统与硬件；
- 下载 URL、大小和 SHA-256；
- 签名或发布者信息；
- 依赖关系和冲突关系；
- 安装后自检命令；
- 回滚版本。

组件管理器流程：

```text
硬件探测
  -> 生成推荐方案
  -> 用户确认磁盘占用
  -> 下载到临时目录
  -> 哈希/签名校验
  -> 原子安装到版本目录
  -> 自检
  -> 切换 current 指针
  -> 失败时回滚
```

不得把不同发行包的 `onnxruntime.dll`、provider DLL 或 TensorRT DLL 复制进同一运行目录。

## 9. 虚拟音频设备与签名

首版默认引导安装 VB-CABLE 等成熟虚拟音频设备，FoxVoice 自动完成设备发现和路由配置。

自研虚拟驱动作为实验组件：

- 仅在需要降低安装摩擦或获得更完整控制时开发；
- 开发阶段使用 Windows 测试签名；
- 开启测试签名可能降低系统安全性并触发游戏反作弊，不作为普通用户默认路径；
- 即使只给朋友使用，正式内核驱动仍建议通过 Microsoft 驱动签名流程；
- 应用程序本体可暂不购买商业代码签名证书，但会遇到 SmartScreen 警告。

## 10. 隐私与安全

- 默认完全离线，联网仅用于用户主动发起的组件或模型下载；
- 不集成账号、遥测、广告和云端推理；
- 日志默认不记录原始音频和完整本地路径；
- 诊断包由用户手动导出，导出前预览和脱敏；
- 模型转换、元数据解析和压缩包解压在低权限隔离进程中执行；
- 所有下载使用固定 HTTPS 来源、哈希校验和可见许可证；
- 全局热键、麦克风访问和管理员操作均提供明确开关。

## 11. 性能目标

首版验收目标：

| 指标 | 目标 |
| --- | --- |
| 实时端到端延迟 | 推荐配置下不高于 120 ms |
| 推理安全余量 | P99 小于块预算的 70% |
| 连续运行 | 4 小时无崩溃、设备泄漏或持续音频中断 |
| 游戏压力测试 | GPU 95% 负载下仍可自动降级并保持可理解语音 |
| 设备恢复 | 默认音频设备变化后 3 秒内恢复或提示 |
| 引擎故障恢复 | 一个输出缓冲周期内进入静音或安全旁路 |
| 配置切换 | 模型和预设切换不阻塞音频回调 |

具体延迟会受模型、块大小、F0 算法、虚拟声卡和聊天软件缓冲影响，因此界面显示实测值，不承诺所有硬件上的固定延迟。

## 12. 实施顺序

### 阶段 A：产品骨架

- 桌面工作台界面；
- 配置、日志和本地数据目录；
- 硬件与音频设备探测；
- 组件清单、安装状态和后端推荐；
- 模型库数据结构与本地导入。

### 阶段 B：最小实时闭环

- WASAPI 输入输出；
- 环形缓冲区和安全旁路；
- 接入固定版本的 `vc-core`；
- WindowsML/DirectML 推理；
- VB-CABLE 路由；
- 延迟、underrun 和推理时间监控。

### 阶段 C：游戏稳定性

- 独立引擎进程；
- 游戏检测与资源限速；
- 自动降级状态机；
- TensorRT 可选组件；
- 无边框、独占全屏和切屏压力测试。

### 阶段 D：完整产品能力

- Hugging Face 导入；
- `.pth` 到 ONNX 转换；
- 音效板与全局热键；
- 单轨离线编辑器；
- 中英文界面；
- 安装、修复、升级和回滚。

### 阶段 E：训练与实验功能

- NVIDIA CUDA 训练；
- 支持矩阵内的 AMD Windows ROCm 训练；
- AMD WSL2/Linux 训练助手；
- 可选 `.index` 检索组件；
- 实验性自研虚拟音频驱动。

## 13. 首个可交付版本范围

第一个可运行版本只承诺：

- Windows 11 x64；
- 本地单人使用；
- WindowsML/DirectML 推理；
- ONNX RVC 模型；
- ContentVec + RMVPE；
- 麦克风输入、监听输出和 VB-CABLE 输出；
- 模型导入、选择和基本元数据；
- 音高、输入增益、输出增益、门限和简单降噪；
- 实时性能面板和安全旁路；
- `.index` 仅保存、不参与实时推理；
- 自动检查并安装缺失的普通运行时依赖。

TensorRT、训练、Hugging Face 浏览、音效板、离线编辑器和自研驱动在实时闭环稳定后逐步加入，避免首版同时引入过多故障源。

## 14. 参考实现与官方资料

- [shirohata/vc-rs](https://github.com/shirohata/vc-rs)
- [vc-rs 架构](https://github.com/shirohata/vc-rs/blob/main/docs/architecture_ja.md)
- [ONNX Runtime DirectML EP](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [ONNX Runtime ROCm EP 移除说明](https://onnxruntime.ai/docs/execution-providers/ROCm-ExecutionProvider.html)
- [AMD Windows ROCm 支持矩阵](https://rocm.docs.amd.com/projects/radeon-ryzen/en/latest/docs/compatibility/compatibilityrad/windows/windows_compatibility.html)
- [FAISS 相关语言绑定](https://github.com/facebookresearch/faiss/wiki/Related-projects)
- [第三方 faiss-rs](https://github.com/Enet4/faiss-rs)
- [w-okada RVC 参数说明](https://github.com/w-okada/voice-changer/blob/master/tutorials/tutorial_rvc_ja_latest.md)
