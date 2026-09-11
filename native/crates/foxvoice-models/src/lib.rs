use std::{
    cmp::Reverse,
    env,
    fs::{self, File, OpenOptions},
    io::{BufReader, Read, Write},
    net::{SocketAddr, TcpStream},
    path::{Path, PathBuf},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use url::Url;

const MANIFEST_FILE: &str = "model.json";
const RECYCLE_DIRECTORY: &str = ".recycle";
const MAX_MODEL_BYTES: u64 = 4 * 1024 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ModelFormat {
    Onnx,
    PytorchCheckpoint,
    FaissIndex,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ModelState {
    Ready,
    ConversionRequired,
    StoredOnly,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelRecord {
    pub id: String,
    pub display_name: String,
    pub format: ModelFormat,
    pub state: ModelState,
    pub file_name: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub imported_at_unix_ms: u64,
    pub source: Option<String>,
    #[serde(default)]
    pub author: Option<String>,
    #[serde(default)]
    pub license: Option<String>,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub rvc_version: Option<String>,
    #[serde(default)]
    pub sample_rate: Option<u32>,
    #[serde(default)]
    pub uses_f0: Option<bool>,
    #[serde(default)]
    pub speaker_count: Option<u32>,
    #[serde(default)]
    pub recommended_provider: Option<String>,
    #[serde(default)]
    pub test_status: Option<String>,
    #[serde(default)]
    pub last_tested_at_unix_ms: Option<u64>,
    #[serde(default)]
    pub last_used_at_unix_ms: Option<u64>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModelMetadataUpdate {
    pub display_name: String,
    pub author: Option<String>,
    pub license: Option<String>,
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HuggingFaceFile {
    pub path: String,
    pub size_bytes: u64,
    pub download_url: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HuggingFaceRepositoryInfo {
    pub repository: String,
    pub revision: String,
    pub license: Option<String>,
    pub gated: bool,
    pub private: bool,
}

#[derive(Debug, Clone)]
pub struct ModelLibrary {
    root: PathBuf,
}

impl ModelLibrary {
    pub fn open(root: impl Into<PathBuf>) -> Result<Self> {
        let root = root.into();
        fs::create_dir_all(&root)
            .with_context(|| format!("无法创建模型库目录: {}", root.display()))?;
        Ok(Self { root })
    }

    pub fn import_file(&self, source_path: &Path, source: Option<String>) -> Result<ModelRecord> {
        let metadata = source_path
            .metadata()
            .with_context(|| format!("无法读取模型文件: {}", source_path.display()))?;
        if !metadata.is_file() {
            bail!("模型来源不是普通文件: {}", source_path.display());
        }
        anyhow::ensure!(metadata.len() > 0, "模型文件为空");
        anyhow::ensure!(metadata.len() <= MAX_MODEL_BYTES, "模型文件超过 4 GiB 上限");

        let file_name = source_path
            .file_name()
            .and_then(|value| value.to_str())
            .context("模型文件名不是有效 UTF-8")?
            .to_owned();
        let (format, state) = classify(source_path)?;
        if format == ModelFormat::Onnx {
            vc_core::model_rvc::inspect_model(source_path)
                .context("ONNX 文件不是受支持的 RVC 模型")?;
        }
        let sha256 = sha256_file(source_path)?;
        let id = format!("model-{}", &sha256[..16]);
        let destination_directory = self.root.join(&id);
        let manifest_path = destination_directory.join(MANIFEST_FILE);
        if manifest_path.exists() {
            return read_manifest(&manifest_path);
        }

        let temporary_directory = self.root.join(format!(".{id}.importing"));
        if temporary_directory.exists() {
            fs::remove_dir_all(&temporary_directory).context("无法清理中断的模型导入目录")?;
        }
        fs::create_dir(&temporary_directory).context("无法创建模型导入临时目录")?;

        fs::copy(source_path, temporary_directory.join(&file_name)).context("无法复制模型文件")?;
        let record = ModelRecord {
            id: id.clone(),
            display_name: source_path
                .file_stem()
                .and_then(|value| value.to_str())
                .unwrap_or("RVC Model")
                .to_owned(),
            format,
            state,
            file_name,
            size_bytes: metadata.len(),
            sha256,
            imported_at_unix_ms: unix_ms()?,
            source,
            author: None,
            license: None,
            tags: default_tags(format),
            rvc_version: None,
            sample_rate: None,
            uses_f0: None,
            speaker_count: None,
            recommended_provider: None,
            test_status: None,
            last_tested_at_unix_ms: None,
            last_used_at_unix_ms: None,
        };
        write_manifest(&temporary_directory.join(MANIFEST_FILE), &record)?;
        fs::rename(&temporary_directory, &destination_directory).context("无法原子提交模型导入")?;
        Ok(record)
    }

    pub fn import_huggingface(&self, source_url: &str) -> Result<ModelRecord> {
        let url = Url::parse(source_url).context("Hugging Face URL 无效")?;
        anyhow::ensure!(url.scheme() == "https", "模型下载只允许 HTTPS");
        anyhow::ensure!(
            url.host_str() == Some("huggingface.co"),
            "只允许从 huggingface.co 下载"
        );
        anyhow::ensure!(
            url.path().contains("/resolve/"),
            "请使用 Hugging Face 文件的 resolve URL"
        );
        let file_name = url
            .path_segments()
            .and_then(Iterator::last)
            .filter(|name| !name.is_empty())
            .context("URL 中缺少模型文件名")?;
        classify(Path::new(file_name))?;

        let download_root = self.root.join(".downloads");
        fs::create_dir_all(&download_root).context("无法创建下载临时目录")?;
        let url_hash = format!("{:x}", Sha256::digest(url.as_str().as_bytes()));
        let temporary_path = download_root.join(format!(".part-{}-{file_name}", &url_hash[..16]));
        let mut completed_download = false;
        let repository_info = self.huggingface_repository_info(source_url).ok();
        let repository = parse_huggingface_repository(source_url)
            .ok()
            .map(|value| value.0);
        let result = (|| -> Result<ModelRecord> {
            let existing = temporary_path
                .metadata()
                .map(|value| value.len())
                .unwrap_or(0);
            anyhow::ensure!(existing <= MAX_MODEL_BYTES, "下载临时文件超过 4 GiB 上限");
            let agent = http_agent()?;
            let mut request = agent.get(url.as_str());
            if existing > 0 {
                request = request.header("Range", format!("bytes={existing}-"));
            }
            let mut response = request.call().context("Hugging Face 下载失败")?;
            let resumed = existing > 0 && response.status().as_u16() == 206;
            let offset = if resumed { existing } else { 0 };
            if let Some(length) = response
                .headers()
                .get("content-length")
                .and_then(|value| value.to_str().ok())
                .and_then(|value| value.parse::<u64>().ok())
            {
                anyhow::ensure!(
                    offset.saturating_add(length) <= MAX_MODEL_BYTES,
                    "远程模型超过 4 GiB 上限"
                );
            }
            let mut reader = response.body_mut().as_reader();
            let mut output = OpenOptions::new()
                .create(true)
                .write(true)
                .append(resumed)
                .truncate(!resumed)
                .open(&temporary_path)
                .context("无法创建下载临时文件")?;
            let copied = std::io::copy(
                &mut reader.by_ref().take(MAX_MODEL_BYTES - offset + 1),
                &mut output,
            )?;
            anyhow::ensure!(
                offset + copied <= MAX_MODEL_BYTES,
                "远程模型超过 4 GiB 上限"
            );
            output.sync_all()?;
            completed_download = true;
            let mut record = self.import_file(&temporary_path, Some(url.to_string()))?;
            if record.author.is_none() {
                record.author = repository
                    .as_deref()
                    .and_then(|value| value.split_once('/'))
                    .map(|value| value.0.to_owned());
            }
            if record.license.is_none() {
                record.license = repository_info
                    .as_ref()
                    .and_then(|value| value.license.clone());
            }
            if !record.tags.iter().any(|value| value == "hugging-face") {
                record.tags.push("hugging-face".into());
            }
            self.save_record(&record)?;
            Ok(record)
        })();
        if (result.is_ok() || completed_download) && temporary_path.exists() {
            fs::remove_file(&temporary_path).context("无法清理下载临时文件")?;
        }
        result
    }

    pub fn list_huggingface_files(&self, repository_url: &str) -> Result<Vec<HuggingFaceFile>> {
        let (repository, revision) = parse_huggingface_repository(repository_url)?;
        let (owner, repo) = repository.split_once('/').expect("validated repository id");
        let mut api_url = Url::parse("https://huggingface.co")?;
        api_url
            .path_segments_mut()
            .map_err(|_| anyhow::anyhow!("无法构造 Hugging Face API URL"))?
            .extend(["api", "models", owner, repo, "tree", &revision]);
        api_url
            .query_pairs_mut()
            .append_pair("recursive", "true")
            .append_pair("expand", "false");
        let mut response = http_agent()?
            .get(api_url.as_str())
            .call()
            .context("无法读取 Hugging Face 仓库文件")?;
        let entries: Vec<HuggingFaceTreeEntry> =
            serde_json::from_reader(response.body_mut().as_reader())
                .context("Hugging Face 仓库文件响应无效")?;
        let mut files: Vec<_> = entries
            .into_iter()
            .filter(|entry| entry.kind == "file")
            .filter(|entry| classify(Path::new(&entry.path)).is_ok())
            .map(|entry| {
                let mut download = Url::parse("https://huggingface.co").expect("static URL");
                download
                    .path_segments_mut()
                    .expect("base URL")
                    .extend([owner, repo, "resolve", &revision]);
                download
                    .path_segments_mut()
                    .expect("base URL")
                    .extend(entry.path.split('/'));
                HuggingFaceFile {
                    download_url: download.to_string(),
                    path: entry.path,
                    size_bytes: entry.size.unwrap_or(0),
                }
            })
            .collect();
        files.sort_by(|left, right| left.path.cmp(&right.path));
        Ok(files)
    }

    pub fn huggingface_repository_info(
        &self,
        repository_url: &str,
    ) -> Result<HuggingFaceRepositoryInfo> {
        let (repository, revision) = parse_huggingface_repository(repository_url)?;
        let mut api_url = Url::parse("https://huggingface.co")?;
        api_url
            .path_segments_mut()
            .map_err(|_| anyhow::anyhow!("无法构造 Hugging Face API URL"))?
            .extend(["api", "models"])
            .extend(repository.split('/'));
        let mut response = http_agent()?
            .get(api_url.as_str())
            .call()
            .context("无法读取 Hugging Face 仓库元数据")?;
        let metadata: serde_json::Value = serde_json::from_reader(response.body_mut().as_reader())
            .context("Hugging Face 仓库元数据响应无效")?;
        let license = metadata
            .get("cardData")
            .and_then(|value| value.get("license"))
            .and_then(|value| value.as_str())
            .map(ToOwned::to_owned);
        Ok(HuggingFaceRepositoryInfo {
            repository,
            revision,
            license,
            gated: metadata.get("gated").is_some_and(|value| match value {
                serde_json::Value::Bool(state) => *state,
                serde_json::Value::String(state) => state != "false" && !state.is_empty(),
                _ => false,
            }),
            private: metadata
                .get("private")
                .and_then(|value| value.as_bool())
                .unwrap_or(false),
        })
    }

    pub fn list(&self) -> Result<Vec<ModelRecord>> {
        let mut records = Vec::new();
        for entry in fs::read_dir(&self.root).context("无法读取模型库")? {
            let entry = entry?;
            if !entry.file_type()?.is_dir() || entry.file_name() == RECYCLE_DIRECTORY {
                continue;
            }
            let manifest = entry.path().join(MANIFEST_FILE);
            if manifest.is_file() {
                records.push(read_manifest(&manifest)?);
            }
        }
        records.sort_by_key(|record| Reverse(record.imported_at_unix_ms));
        Ok(records)
    }

    pub fn recycle(&self, id: &str) -> Result<PathBuf> {
        validate_id(id)?;
        let source = self.root.join(id);
        anyhow::ensure!(source.is_dir(), "模型不存在: {id}");
        let recycle_root = self.root.join(RECYCLE_DIRECTORY);
        fs::create_dir_all(&recycle_root).context("无法创建模型回收目录")?;
        let destination = recycle_root.join(format!("{id}-{}", unix_ms()?));
        fs::rename(&source, &destination).context("无法将模型移动到回收区")?;
        Ok(destination)
    }

    pub fn model_file(&self, id: &str) -> Result<PathBuf> {
        validate_id(id)?;
        let directory = self.root.join(id);
        let record = read_manifest(&directory.join(MANIFEST_FILE))?;
        let path = directory.join(record.file_name);
        anyhow::ensure!(path.is_file(), "模型数据文件缺失: {id}");
        Ok(path)
    }

    pub fn update_metadata(&self, id: &str, update: ModelMetadataUpdate) -> Result<ModelRecord> {
        let mut record = self.read_record(id)?;
        let display_name = update.display_name.trim();
        anyhow::ensure!(!display_name.is_empty(), "模型名称不能为空");
        anyhow::ensure!(
            display_name.chars().count() <= 120,
            "模型名称不能超过 120 个字符"
        );
        record.display_name = display_name.to_owned();
        record.author = normalize_optional(update.author, 120, "作者")?;
        record.license = normalize_optional(update.license, 80, "许可证")?;
        record.tags = normalize_tags(update.tags)?;
        self.save_record(&record)?;
        Ok(record)
    }

    pub fn mark_used(&self, id: &str) -> Result<ModelRecord> {
        let mut record = self.read_record(id)?;
        record.last_used_at_unix_ms = Some(unix_ms()?);
        self.save_record(&record)?;
        Ok(record)
    }

    pub fn record_test(&self, id: &str, passed: bool, provider: &str) -> Result<ModelRecord> {
        let mut record = self.read_record(id)?;
        let provider = provider.trim().to_ascii_lowercase();
        anyhow::ensure!(
            matches!(provider.as_str(), "directml" | "nvtrtx" | "cpu"),
            "未知推理后端"
        );
        record.test_status = Some(if passed { "passed" } else { "failed" }.into());
        record.last_tested_at_unix_ms = Some(unix_ms()?);
        if passed {
            record.recommended_provider = Some(provider);
        }
        self.save_record(&record)?;
        Ok(record)
    }

    fn read_record(&self, id: &str) -> Result<ModelRecord> {
        validate_id(id)?;
        read_manifest(&self.root.join(id).join(MANIFEST_FILE))
    }

    fn save_record(&self, record: &ModelRecord) -> Result<()> {
        validate_id(&record.id)?;
        let directory = self.root.join(&record.id);
        anyhow::ensure!(directory.is_dir(), "模型不存在: {}", record.id);
        write_manifest(&directory.join(MANIFEST_FILE), record)
    }
}

fn default_tags(format: ModelFormat) -> Vec<String> {
    match format {
        ModelFormat::Onnx => vec!["rvc".into(), "onnx".into()],
        ModelFormat::PytorchCheckpoint => vec!["rvc".into(), "checkpoint".into()],
        ModelFormat::FaissIndex => vec!["rvc".into(), "index".into()],
    }
}

fn normalize_optional(
    value: Option<String>,
    maximum: usize,
    label: &str,
) -> Result<Option<String>> {
    let value = value
        .map(|text| text.trim().to_owned())
        .filter(|text| !text.is_empty());
    if let Some(text) = &value {
        anyhow::ensure!(
            text.chars().count() <= maximum,
            "{label}不能超过 {maximum} 个字符"
        );
    }
    Ok(value)
}

fn normalize_tags(tags: Vec<String>) -> Result<Vec<String>> {
    anyhow::ensure!(tags.len() <= 20, "标签不能超过 20 个");
    let mut normalized = Vec::new();
    for tag in tags {
        let tag = tag.trim().to_ascii_lowercase();
        if tag.is_empty() || normalized.contains(&tag) {
            continue;
        }
        anyhow::ensure!(tag.chars().count() <= 32, "单个标签不能超过 32 个字符");
        normalized.push(tag);
    }
    Ok(normalized)
}

#[derive(Debug, Deserialize)]
struct HuggingFaceTreeEntry {
    #[serde(rename = "type")]
    kind: String,
    path: String,
    size: Option<u64>,
}

fn parse_huggingface_repository(repository_url: &str) -> Result<(String, String)> {
    let url = Url::parse(repository_url).context("Hugging Face 仓库 URL 无效")?;
    anyhow::ensure!(
        url.scheme() == "https" && url.host_str() == Some("huggingface.co"),
        "只允许 huggingface.co 的 HTTPS 仓库地址"
    );
    let segments: Vec<_> = url
        .path_segments()
        .into_iter()
        .flatten()
        .filter(|part| !part.is_empty())
        .collect();
    anyhow::ensure!(
        segments.len() == 2
            || (segments.len() == 4 && segments[2] == "tree")
            || (segments.len() >= 5 && segments[2] == "resolve"),
        "请使用 huggingface.co 的仓库、tree/分支或 resolve 文件地址"
    );
    let revision = if segments.len() >= 4 {
        segments[3]
    } else {
        "main"
    };
    anyhow::ensure!(
        segments[0] != "." && segments[0] != ".." && segments[1] != "." && segments[1] != "..",
        "仓库标识不安全"
    );
    Ok((
        format!("{}/{}", segments[0], segments[1]),
        revision.to_owned(),
    ))
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

fn classify(path: &Path) -> Result<(ModelFormat, ModelState)> {
    match path
        .extension()
        .and_then(|value| value.to_str())
        .map(str::to_ascii_lowercase)
        .as_deref()
    {
        Some("onnx") => Ok((ModelFormat::Onnx, ModelState::Ready)),
        Some("pth") => Ok((
            ModelFormat::PytorchCheckpoint,
            ModelState::ConversionRequired,
        )),
        Some("index") => Ok((ModelFormat::FaissIndex, ModelState::StoredOnly)),
        _ => bail!("仅支持 .onnx、.pth 和 .index 文件"),
    }
}

fn validate_id(id: &str) -> Result<()> {
    anyhow::ensure!(
        id.starts_with("model-")
            && id.len() == 22
            && id[6..].bytes().all(|byte| byte.is_ascii_hexdigit()),
        "无效模型 ID"
    );
    Ok(())
}

fn sha256_file(path: &Path) -> Result<String> {
    let mut reader = BufReader::new(File::open(path)?);
    let mut hasher = Sha256::new();
    let mut buffer = vec![0_u8; 1024 * 1024];
    loop {
        let length = reader.read(&mut buffer)?;
        if length == 0 {
            break;
        }
        hasher.update(&buffer[..length]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

fn read_manifest(path: &Path) -> Result<ModelRecord> {
    serde_json::from_reader(BufReader::new(File::open(path)?))
        .with_context(|| format!("模型清单损坏: {}", path.display()))
}

fn write_manifest(path: &Path, record: &ModelRecord) -> Result<()> {
    let mut file = File::create(path)?;
    serde_json::to_writer_pretty(&mut file, record)?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    Ok(())
}

fn unix_ms() -> Result<u64> {
    Ok(SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .context("系统时间早于 Unix epoch")?
        .as_millis() as u64)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn imports_lists_deduplicates_and_recycles() {
        let temporary = tempfile::tempdir().unwrap();
        let source = temporary.path().join("voice.pth");
        fs::write(&source, b"fake-pth-for-library-test").unwrap();
        let library = ModelLibrary::open(temporary.path().join("models")).unwrap();

        let first = library
            .import_file(&source, Some("local-test".into()))
            .unwrap();
        let duplicate = library.import_file(&source, None).unwrap();
        assert_eq!(first.id, duplicate.id);
        assert_eq!(library.list().unwrap(), vec![first.clone()]);
        assert_eq!(
            library.model_file(&first.id).unwrap(),
            library.root.join(&first.id).join(&first.file_name)
        );

        let recycled = library.recycle(&first.id).unwrap();
        assert!(recycled.is_dir());
        assert!(library.list().unwrap().is_empty());
    }

    #[test]
    fn classifies_supported_rvc_assets() {
        assert_eq!(
            classify(Path::new("voice.pth")).unwrap(),
            (
                ModelFormat::PytorchCheckpoint,
                ModelState::ConversionRequired
            )
        );
        assert_eq!(
            classify(Path::new("voice.index")).unwrap(),
            (ModelFormat::FaissIndex, ModelState::StoredOnly)
        );
        assert!(classify(Path::new("voice.exe")).is_err());
    }

    #[test]
    fn rejects_path_traversal_as_model_id() {
        assert!(validate_id("../model-deadbeefdeadbeef").is_err());
    }

    #[test]
    fn rejects_invalid_onnx_before_copying_it() {
        let temporary = tempfile::tempdir().unwrap();
        let source = temporary.path().join("broken.onnx");
        fs::write(&source, b"not-an-onnx-model").unwrap();
        let model_root = temporary.path().join("models");
        let library = ModelLibrary::open(&model_root).unwrap();
        assert!(library.import_file(&source, None).is_err());
        assert!(library.list().unwrap().is_empty());
    }

    #[test]
    fn reads_legacy_manifest_and_persists_metadata_activity() {
        let temporary = tempfile::tempdir().unwrap();
        let root = temporary.path().join("models");
        let id = "model-deadbeefdeadbeef";
        let directory = root.join(id);
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("voice.pth"), b"checkpoint").unwrap();
        fs::write(
            directory.join(MANIFEST_FILE),
            format!(
                r#"{{
  "id": "{id}",
  "displayName": "Legacy",
  "format": "pytorchCheckpoint",
  "state": "conversionRequired",
  "fileName": "voice.pth",
  "sizeBytes": 10,
  "sha256": "deadbeef",
  "importedAtUnixMs": 1,
  "source": null
}}"#
            ),
        )
        .unwrap();
        let library = ModelLibrary::open(&root).unwrap();
        let legacy = library.list().unwrap().remove(0);
        assert!(legacy.tags.is_empty());
        assert!(legacy.last_used_at_unix_ms.is_none());

        let updated = library
            .update_metadata(
                id,
                ModelMetadataUpdate {
                    display_name: "  New voice  ".into(),
                    author: Some(" Fox ".into()),
                    license: Some(" MIT ".into()),
                    tags: vec!["RVC".into(), "game".into(), "rvc".into()],
                },
            )
            .unwrap();
        assert_eq!(updated.display_name, "New voice");
        assert_eq!(updated.author.as_deref(), Some("Fox"));
        assert_eq!(updated.tags, vec!["rvc", "game"]);
        assert!(
            library
                .mark_used(id)
                .unwrap()
                .last_used_at_unix_ms
                .is_some()
        );
        let tested = library.record_test(id, true, "DirectML").unwrap();
        assert_eq!(tested.test_status.as_deref(), Some("passed"));
        assert_eq!(tested.recommended_provider.as_deref(), Some("directml"));
        assert!(tested.last_tested_at_unix_ms.is_some());
    }

    #[test]
    fn rejects_invalid_metadata() {
        let temporary = tempfile::tempdir().unwrap();
        let source = temporary.path().join("voice.pth");
        fs::write(&source, b"checkpoint").unwrap();
        let library = ModelLibrary::open(temporary.path().join("models")).unwrap();
        let model = library.import_file(&source, None).unwrap();
        assert!(
            library
                .update_metadata(
                    &model.id,
                    ModelMetadataUpdate {
                        display_name: " ".into(),
                        author: None,
                        license: None,
                        tags: vec![],
                    }
                )
                .is_err()
        );
        assert!(library.record_test(&model.id, true, "mystery").is_err());
    }

    #[test]
    fn only_accepts_huggingface_resolve_urls() {
        let temporary = tempfile::tempdir().unwrap();
        let library = ModelLibrary::open(temporary.path().join("models")).unwrap();
        assert!(
            library
                .import_huggingface("http://huggingface.co/a/b/resolve/main/a.pth")
                .is_err()
        );
        assert!(
            library
                .import_huggingface("https://example.com/a.pth")
                .is_err()
        );
        assert!(
            library
                .import_huggingface("https://huggingface.co/a/b/blob/main/a.pth")
                .is_err()
        );
    }

    #[test]
    fn parses_huggingface_repository_and_revision_urls() {
        assert_eq!(
            parse_huggingface_repository("https://huggingface.co/owner/voice-model").unwrap(),
            ("owner/voice-model".into(), "main".into())
        );
        assert_eq!(
            parse_huggingface_repository("https://huggingface.co/owner/voice-model/tree/dev")
                .unwrap(),
            ("owner/voice-model".into(), "dev".into())
        );
        assert_eq!(
            parse_huggingface_repository(
                "https://huggingface.co/owner/voice-model/resolve/release/models/voice.onnx"
            )
            .unwrap(),
            ("owner/voice-model".into(), "release".into())
        );
        assert!(parse_huggingface_repository("https://example.com/owner/model").is_err());
        assert!(parse_huggingface_repository("https://huggingface.co/owner").is_err());
    }
}
