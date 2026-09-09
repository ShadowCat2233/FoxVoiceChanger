use std::{
    collections::HashSet,
    fs,
    path::{Component, Path},
};

use foxvoice_contracts::{EngineBackend, GpuVendor};
use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ComponentManifest {
    pub schema_version: u32,
    pub components: Vec<ComponentSpec>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ComponentSpec {
    pub id: String,
    pub version: String,
    pub kind: ComponentKind,
    pub backend: Option<EngineBackend>,
    pub install_directory: String,
    pub conflict_group: Option<String>,
    pub required: bool,
    pub supported_vendors: Vec<GpuVendor>,
    pub artifacts: Vec<ComponentArtifact>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ComponentKind {
    Engine,
    Tool,
    AudioDriver,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ComponentArtifact {
    pub file_name: String,
    pub sha256: String,
    pub size_bytes: u64,
}

#[derive(Debug, Error)]
pub enum ManifestError {
    #[error("无法读取组件清单: {0}")]
    Read(#[from] std::io::Error),
    #[error("组件清单不是有效 JSON: {0}")]
    Parse(#[from] serde_json::Error),
    #[error("不支持的组件清单版本 {0}")]
    UnsupportedSchema(u32),
    #[error("重复的组件 ID: {0}")]
    DuplicateId(String),
    #[error("重复的安装目录: {0}")]
    DuplicateInstallDirectory(String),
    #[error("组件 {0} 的安装目录不安全")]
    UnsafeInstallDirectory(String),
    #[error("推理引擎 {0} 必须属于 realtime-engine 冲突组")]
    MissingEngineIsolation(String),
    #[error("缺少必需的 fox-engine-windowsml 组件")]
    MissingDefaultEngine,
}

impl ComponentManifest {
    pub fn load(path: impl AsRef<Path>) -> Result<Self, ManifestError> {
        let bytes = fs::read(path)?;
        let manifest: Self = serde_json::from_slice(&bytes)?;
        manifest.validate()?;
        Ok(manifest)
    }

    pub fn validate(&self) -> Result<(), ManifestError> {
        if self.schema_version != 1 {
            return Err(ManifestError::UnsupportedSchema(self.schema_version));
        }

        let mut ids = HashSet::new();
        let mut directories = HashSet::new();
        let mut has_default = false;

        for component in &self.components {
            if !ids.insert(component.id.as_str()) {
                return Err(ManifestError::DuplicateId(component.id.clone()));
            }
            if !directories.insert(component.install_directory.as_str()) {
                return Err(ManifestError::DuplicateInstallDirectory(
                    component.install_directory.clone(),
                ));
            }
            if !is_safe_relative_directory(&component.install_directory) {
                return Err(ManifestError::UnsafeInstallDirectory(component.id.clone()));
            }
            if component.kind == ComponentKind::Engine
                && component.conflict_group.as_deref() != Some("realtime-engine")
            {
                return Err(ManifestError::MissingEngineIsolation(component.id.clone()));
            }
            if component.id == "fox-engine-windowsml"
                && component.required
                && component.backend == Some(EngineBackend::WindowsMl)
            {
                has_default = true;
            }
        }

        if !has_default {
            return Err(ManifestError::MissingDefaultEngine);
        }
        Ok(())
    }
}

fn is_safe_relative_directory(value: &str) -> bool {
    let path = Path::new(value);
    !path.is_absolute()
        && !value.is_empty()
        && path
            .components()
            .all(|component| matches!(component, Component::Normal(_)))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn valid_manifest() -> ComponentManifest {
        ComponentManifest {
            schema_version: 1,
            components: vec![ComponentSpec {
                id: "fox-engine-windowsml".into(),
                version: "0.1.0".into(),
                kind: ComponentKind::Engine,
                backend: Some(EngineBackend::WindowsMl),
                install_directory: "engines/windowsml/0.1.0".into(),
                conflict_group: Some("realtime-engine".into()),
                required: true,
                supported_vendors: vec![GpuVendor::Unknown],
                artifacts: Vec::new(),
            }],
        }
    }

    #[test]
    fn accepts_isolated_default_engine() {
        assert!(valid_manifest().validate().is_ok());
    }

    #[test]
    fn rejects_parent_directory_escape() {
        let mut manifest = valid_manifest();
        manifest.components[0].install_directory = "../shared".into();
        assert!(matches!(
            manifest.validate(),
            Err(ManifestError::UnsafeInstallDirectory(_))
        ));
    }
}
