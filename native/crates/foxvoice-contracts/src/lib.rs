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

pub const IPC_PROTOCOL_VERSION: u16 = 1;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlRequest {
    pub protocol_version: u16,
    pub request_id: u64,
    pub command: ControlCommand,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum ControlCommand {
    Doctor,
    ListAudioDevices,
    StartBypass {
        input_device_id: Option<String>,
        output_device_id: Option<String>,
        buffer_ms: u32,
    },
    StopAudio,
    StartVoice {
        model_id: String,
    },
    SetGameGuard {
        enabled: bool,
    },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum ControlEvent {
    Accepted {
        request_id: u64,
    },
    AudioStateChanged {
        running: bool,
        mode: AudioMode,
    },
    Performance {
        sample: PerformanceSample,
        guard: GuardDecision,
    },
    Error {
        request_id: Option<u64>,
        code: String,
        message: String,
    },
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AudioMode {
    #[default]
    Stopped,
    SafeBypass,
    VoiceConversion,
    Muted,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AudioDeviceDirection {
    Input,
    Output,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioDeviceInfo {
    pub id: String,
    pub name: String,
    pub direction: AudioDeviceDirection,
    pub is_default: bool,
    pub channels: Option<u16>,
    pub sample_rate: Option<u32>,
    pub sample_format: Option<String>,
}
