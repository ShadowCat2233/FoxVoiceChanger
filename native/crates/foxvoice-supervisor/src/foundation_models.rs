use std::{
    env,
    fs::{self, File},
    io::{BufReader, Read, Write},
    net::{SocketAddr, TcpStream},
    path::{Path, PathBuf},
    time::Duration,
};

use anyhow::{Context, Result, bail};
use serde::Serialize;
use sha2::{Digest, Sha256};

const MAX_DOWNLOAD_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Debug, Clone, Copy)]
struct FoundationSpec {
    id: &'static str,
    file_name: &'static str,
    url: &'static str,
    sha256: &'static str,
    size_bytes: u64,
}

const FOUNDATION_MODELS: [FoundationSpec; 2] = [
    FoundationSpec {
        id: "contentvec",
        file_name: "content_vec_500.onnx",
        url: "https://huggingface.co/wok000/weights_gpl/resolve/main/content-vec/contentvec-f.onnx",
        sha256: "4b31ed3d95a568fab7952de923ff7f7d3d17128ea6fce69f665509d24c3156db",
        size_bytes: 378_550_151,
    },
    FoundationSpec {
        id: "rmvpe",
        file_name: "rmvpe.onnx",
        url: "https://huggingface.co/wok000/weights_gpl/resolve/main/rmvpe/rmvpe_20231006.onnx",
        sha256: "84f0586308e36157f75b77c8591bf636d6719c0c4ba95f8faf3df479e7566219",
        size_bytes: 362_003_174,
    },
];

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FoundationModelStatus {
    pub id: &'static str,
    pub file_name: &'static str,
    pub path: PathBuf,
    pub installed: bool,
    pub verified: bool,
    pub expected_size_bytes: u64,
    pub expected_sha256: &'static str,
    pub source: &'static str,
    pub license: &'static str,
}

pub fn foundation_root() -> Result<PathBuf> {
    if let Some(path) = env::var_os("FOXVOICE_DATA_DIR") {
        return Ok(PathBuf::from(path)
            .join("components")
            .join("rvc-foundation"));
    }
    let local = env::var_os("LOCALAPPDATA").context("系统缺少 LOCALAPPDATA")?;
    Ok(PathBuf::from(local)
        .join("FoxVoice")
        .join("components")
        .join("rvc-foundation"))
}

pub fn status(root: &Path) -> Result<Vec<FoundationModelStatus>> {
    FOUNDATION_MODELS
        .iter()
        .map(|spec| {
            let path = root.join(spec.file_name);
            let installed = path.is_file();
            let verified = installed
                && path.metadata()?.len() == spec.size_bytes
                && sha256_file(&path)? == spec.sha256;
            Ok(FoundationModelStatus {
                id: spec.id,
                file_name: spec.file_name,
                path,
                installed,
                verified,
                expected_size_bytes: spec.size_bytes,
                expected_sha256: spec.sha256,
                source: spec.url,
                license: "GPL-3.0 (wok000/weights_gpl; not bundled with FoxVoice)",
            })
        })
        .collect()
}

pub fn install(root: &Path, accepted_gpl: bool) -> Result<Vec<FoundationModelStatus>> {
    if !accepted_gpl {
        bail!("安装已取消：必须明确接受上游 GPL-3.0 权重许可（--accept-gpl）");
    }
    fs::create_dir_all(root).context("无法创建基础模型目录")?;
    for spec in FOUNDATION_MODELS {
        let destination = root.join(spec.file_name);
        if destination.is_file()
            && destination.metadata()?.len() == spec.size_bytes
            && sha256_file(&destination)? == spec.sha256
        {
            continue;
        }
        let temporary = root.join(format!(".{}.download", spec.file_name));
        if temporary.exists() {
            fs::remove_file(&temporary).context("无法清理上次中断的下载")?;
        }
        let result = download_verified(spec, &temporary, &destination);
        if temporary.exists() {
            let _ = fs::remove_file(&temporary);
        }
        result?;
    }
    status(root)
}

fn download_verified(spec: FoundationSpec, temporary: &Path, destination: &Path) -> Result<()> {
    let mut response = http_agent()?
        .get(spec.url)
        .call()
        .with_context(|| format!("{} 下载失败", spec.id))?;
    if let Some(length) = response
        .headers()
        .get("content-length")
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<u64>().ok())
    {
        anyhow::ensure!(
            length == spec.size_bytes,
            "{} 远程文件长度与清单不符",
            spec.id
        );
    }
    let mut input = response.body_mut().as_reader();
    let mut output = File::create(temporary).context("无法创建基础模型临时文件")?;
    let copied = std::io::copy(
        &mut input.by_ref().take(MAX_DOWNLOAD_BYTES + 1),
        &mut output,
    )?;
    anyhow::ensure!(copied <= MAX_DOWNLOAD_BYTES, "{} 超过下载大小上限", spec.id);
    anyhow::ensure!(copied == spec.size_bytes, "{} 下载不完整", spec.id);
    output.flush()?;
    output.sync_all()?;
    anyhow::ensure!(
        sha256_file(temporary)? == spec.sha256,
        "{} SHA-256 校验失败",
        spec.id
    );
    fs::rename(temporary, destination).context("无法原子提交基础模型")?;
    Ok(())
}

fn http_agent() -> Result<ureq::Agent> {
    if let Some(proxy) = env::var_os("FOXVOICE_PROXY") {
        let proxy =
            ureq::Proxy::new(proxy.to_string_lossy().as_ref()).context("FOXVOICE_PROXY 无效")?;
        return Ok(ureq::Agent::config_builder()
            .proxy(Some(proxy))
            .build()
            .into());
    }
    if [
        "ALL_PROXY",
        "HTTPS_PROXY",
        "HTTP_PROXY",
        "all_proxy",
        "https_proxy",
        "http_proxy",
    ]
    .iter()
    .any(|name| env::var_os(name).is_some())
    {
        return Ok(ureq::Agent::new_with_defaults());
    }
    for port in [7897, 7890] {
        let address = SocketAddr::from(([127, 0, 0, 1], port));
        if TcpStream::connect_timeout(&address, Duration::from_millis(80)).is_ok() {
            let proxy_url = format!("http://127.0.0.1:{port}");
            let proxy = ureq::Proxy::new(&proxy_url)?;
            return Ok(ureq::Agent::config_builder()
                .proxy(Some(proxy))
                .build()
                .into());
        }
    }
    Ok(ureq::Agent::new_with_defaults())
}

fn sha256_file(path: &Path) -> Result<String> {
    let mut input = BufReader::new(File::open(path)?);
    let mut hasher = Sha256::new();
    let mut buffer = vec![0_u8; 1024 * 1024];
    loop {
        let length = input.read(&mut buffer)?;
        if length == 0 {
            break;
        }
        hasher.update(&buffer[..length]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_models_are_reported_without_downloading() {
        let directory = tempfile::tempdir().unwrap();
        let states = status(directory.path()).unwrap();
        assert_eq!(states.len(), 2);
        assert!(
            states
                .iter()
                .all(|state| !state.installed && !state.verified)
        );
    }

    #[test]
    fn install_requires_explicit_license_acceptance() {
        let directory = tempfile::tempdir().unwrap();
        let error = install(directory.path(), false).unwrap_err();
        assert!(error.to_string().contains("--accept-gpl"));
    }
}
