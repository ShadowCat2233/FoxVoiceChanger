# FoxVoice 本地构建验收记录

验收日期：2026-09-10  
平台：Windows 11 x64，NVIDIA GeForce RTX 4060 Ti

本文件只证明当前构建和已接入路径，不再把未提供权重、驱动或压力环境的项目写成产品完成。

| 范围 | 验证结果 |
| --- | --- |
| 桌面界面 | Release 构建 0 警告、0 错误；布局与交互原型一致；五个导航均完成真实点击测试 |
| 启动恢复 | 发布目录与仅含 `FoxVoice.exe` 的隔离目录均能启动并保持响应；原生组件及 Windows ML bootstrapper 可自动提取 |
| 音频路由 | 本机枚举 8 个 WASAPI 端点；安全旁路达到 48 kHz `Running` 并可正常停止 |
| 实时控制 | 运行中发送音高、输出增益和噪声门参数后，引擎继续报告 `Running` |
| 模型管理 | 本地/Hugging Face 单文件导入、SHA-256 去重、结构门禁、列表、解析和回收由测试覆盖 |
| RVC 真实自检 | 用户提供 Generator + 已校验 ContentVec/RMVPE：DirectML 100 帧基准平均 28.2 ms、P95 30.7 ms、P99/最大 35.2 ms，并产生非静音 48 kHz 输出 |
| 虚拟麦克风链路 | 麦克风 → RVC → VB-CABLE 连续运行 12 秒达到 `Running`；稳态处理约 34–41 ms，54 块期间启动后的 underrun/overrun 计数不再增长 |
| 游戏稳定性 | 处理耗时/欠载/流错误连续采样驱动 160/240/320 ms 分级配置与保护旁路；前台全屏/无边框检测会主动切稳定档并暂停新后台重任务 |
| 原生质量门 | MSVC 工作区 29 个测试通过；Clippy `-D warnings` 通过 |
| 发布 | 临时目录构建、原子替换、文件哈希、便携 ZIP 和自动冒烟测试通过 |

## 尚不能验收的项目

- 用户提供的 Generator 未附可独立核验的来源许可证，因此这里只证明技术链路，不替用户确认模型使用权；
- VB-CABLE 主输出已完成短时协议级联调，但尚未在 Discord/具体游戏中完成听感、设备选择与本地双输出监听验收；
- 已完成无游戏负载的 100 帧 P95/P99 基准；未执行 4 小时连续运行、GPU 95% 游戏压力和人工设备热插拔测试；
- 隔离训练环境、官方工作台和 FoxVoice 原生一键训练四阶段编排已接入，但尚未下载数 GB 环境并使用授权数据实际完成一次训练；TensorRT RTX 可按需安装但本机仍为 `NotPresent`；RVC v1、非 F0 检查点仍不支持转换。

## 可重复验证命令

```powershell
.\scripts\bootstrap-native.ps1
.\scripts\build-release.ps1
.\scripts\smoke-release.ps1
```

完成模型来源确认、TensorRT 按需自检以及真实游戏/语音软件听感与长时压力测试后，才能给出完整产品验收结论。
