use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum GpuVendor {
    Nvidia,
    Amd,
    Intel,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum EngineBackend {
    WindowsMl,
    TensorRt,
    Cuda,
    Cpu,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GraphicsAdapter {
    pub name: String,
    pub vendor: GpuVendor,
    pub driver_version: Option<String>,
    pub dedicated_memory_mb: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HardwareProfile {
    pub operating_system: String,
    pub architecture: String,
    pub adapters: Vec<GraphicsAdapter>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineAvailability {
    pub windows_ml: bool,
    pub tensor_rt: bool,
    pub cuda: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineRecommendation {
    pub backend: EngineBackend,
    pub reason: String,
    pub alternatives: Vec<EngineBackend>,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum GuardLevel {
    #[default]
    Normal,
    ReducedVisuals,
    ReducedEffects,
    ExpandedBuffer,
    LightweightModel,
    SafeBypass,
}

impl GuardLevel {
    pub const fn severity(self) -> u8 {
        match self {
            Self::Normal => 0,
            Self::ReducedVisuals => 1,
            Self::ReducedEffects => 2,
            Self::ExpandedBuffer => 3,
            Self::LightweightModel => 4,
            Self::SafeBypass => 5,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PerformanceSample {
    pub inference_ms: f32,
    pub chunk_budget_ms: f32,
    pub gpu_load_percent: f32,
    pub underruns: u32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GuardDecision {
    pub level: GuardLevel,
    pub budget_ratio: f32,
    pub reason: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DoctorReport {
    pub profile: HardwareProfile,
    pub recommendation: EngineRecommendation,
    pub checks: Vec<DoctorCheck>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DoctorCheck {
    pub id: String,
    pub ok: bool,
    pub message: String,
}
