use foxvoice_contracts::{
    EngineAvailability, EngineBackend, EngineRecommendation, GpuVendor, HardwareProfile,
};

pub fn recommend_engine(
    profile: &HardwareProfile,
    available: &EngineAvailability,
) -> EngineRecommendation {
    let has_nvidia = profile
        .adapters
        .iter()
        .any(|gpu| gpu.vendor == GpuVendor::Nvidia);

    if has_nvidia && available.tensor_rt {
        return EngineRecommendation {
            backend: EngineBackend::TensorRt,
            reason: "检测到 NVIDIA GPU，且独立 TensorRT 组件已通过自检。".into(),
            alternatives: fallback_order(available, EngineBackend::TensorRt),
        };
    }

    if available.windows_ml {
        return EngineRecommendation {
            backend: EngineBackend::WindowsMl,
            reason: if has_nvidia {
                "TensorRT 组件未就绪，使用 WindowsML 保证兼容性。".into()
            } else {
                "WindowsML 可覆盖 AMD、Intel、NVIDIA 与核显，是默认兼容路径。".into()
            },
            alternatives: fallback_order(available, EngineBackend::WindowsMl),
        };
    }

    if has_nvidia && available.cuda {
        return EngineRecommendation {
            backend: EngineBackend::Cuda,
            reason: "WindowsML 不可用，使用已验证的 CUDA 组件。".into(),
            alternatives: vec![EngineBackend::Cpu],
        };
    }

    EngineRecommendation {
        backend: EngineBackend::Cpu,
        reason: "未找到通过自检的 GPU 引擎，进入 CPU 安全回退。".into(),
        alternatives: Vec::new(),
    }
}

fn fallback_order(available: &EngineAvailability, selected: EngineBackend) -> Vec<EngineBackend> {
    let mut result = Vec::new();
    if available.windows_ml && selected != EngineBackend::WindowsMl {
        result.push(EngineBackend::WindowsMl);
    }
    if available.cuda && selected != EngineBackend::Cuda {
        result.push(EngineBackend::Cuda);
    }
    result.push(EngineBackend::Cpu);
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use foxvoice_contracts::GraphicsAdapter;

    fn profile(vendor: GpuVendor) -> HardwareProfile {
        HardwareProfile {
            operating_system: "Windows 11".into(),
            architecture: "x86_64".into(),
            adapters: vec![GraphicsAdapter {
                name: "Test GPU".into(),
                vendor,
                driver_version: None,
                dedicated_memory_mb: None,
            }],
        }
    }

    #[test]
    fn nvidia_prefers_verified_tensorrt() {
        let result = recommend_engine(
            &profile(GpuVendor::Nvidia),
            &EngineAvailability {
                windows_ml: true,
                tensor_rt: true,
                cuda: true,
            },
        );
        assert_eq!(result.backend, EngineBackend::TensorRt);
    }

    #[test]
    fn amd_uses_windows_ml() {
        let result = recommend_engine(
            &profile(GpuVendor::Amd),
            &EngineAvailability {
                windows_ml: true,
                tensor_rt: false,
                cuda: false,
            },
        );
        assert_eq!(result.backend, EngineBackend::WindowsMl);
    }

    #[test]
    fn no_verified_runtime_falls_back_to_cpu() {
        let result = recommend_engine(
            &profile(GpuVendor::Unknown),
            &EngineAvailability {
                windows_ml: false,
                tensor_rt: false,
                cuda: false,
            },
        );
        assert_eq!(result.backend, EngineBackend::Cpu);
    }
}
