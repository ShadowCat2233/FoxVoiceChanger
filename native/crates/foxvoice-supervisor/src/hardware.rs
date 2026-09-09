use std::process::Command;

use anyhow::{Context, Result};
use foxvoice_contracts::{GpuVendor, GraphicsAdapter, HardwareProfile};
use serde::Deserialize;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct CimAdapter {
    name: String,
    driver_version: Option<String>,
    adapter_ram: Option<u64>,
}

pub fn detect_hardware() -> Result<HardwareProfile> {
    Ok(HardwareProfile {
        operating_system: detect_os_name(),
        architecture: std::env::consts::ARCH.into(),
        adapters: detect_graphics_adapters().unwrap_or_default(),
    })
}

fn detect_os_name() -> String {
    #[cfg(target_os = "windows")]
    {
        let output = Command::new("cmd").args(["/C", "ver"]).output();
        if let Ok(output) = output {
            let value = String::from_utf8_lossy(&output.stdout).trim().to_string();
            if !value.is_empty() {
                return value;
            }
        }
    }
    format!("{} {}", std::env::consts::OS, std::env::consts::ARCH)
}

fn detect_graphics_adapters() -> Result<Vec<GraphicsAdapter>> {
    #[cfg(not(target_os = "windows"))]
    return Ok(Vec::new());

    #[cfg(target_os = "windows")]
    {
        const SCRIPT: &str = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new(); ConvertTo-Json -InputObject @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,AdapterRAM) -Compress";
        let output = Command::new("powershell")
            .args(["-NoProfile", "-NonInteractive", "-Command", SCRIPT])
            .output()
            .context("无法启动 PowerShell 硬件探测")?;
        anyhow::ensure!(output.status.success(), "WMI 图形适配器探测失败");
        let adapters: Vec<CimAdapter> =
            serde_json::from_slice(&output.stdout).context("无法解析图形适配器信息")?;
        Ok(adapters
            .into_iter()
            .map(|gpu| GraphicsAdapter {
                vendor: classify_vendor(&gpu.name),
                name: gpu.name,
                driver_version: gpu.driver_version,
                dedicated_memory_mb: gpu.adapter_ram.map(|bytes| bytes / 1024 / 1024),
            })
            .collect())
    }
}

fn classify_vendor(name: &str) -> GpuVendor {
    let normalized = name.to_ascii_lowercase();
    if normalized.contains("nvidia") {
        GpuVendor::Nvidia
    } else if normalized.contains("amd") || normalized.contains("radeon") {
        GpuVendor::Amd
    } else if normalized.contains("intel") {
        GpuVendor::Intel
    } else {
        GpuVendor::Unknown
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recognizes_common_gpu_names() {
        assert_eq!(
            classify_vendor("NVIDIA GeForce RTX 4070"),
            GpuVendor::Nvidia
        );
        assert_eq!(classify_vendor("AMD Radeon RX 7900 XTX"), GpuVendor::Amd);
        assert_eq!(classify_vendor("Intel Arc A770"), GpuVendor::Intel);
    }
}
