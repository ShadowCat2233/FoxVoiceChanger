use std::{
    cmp::Reverse,
    fs::{self, File},
    io::{BufReader, Read, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

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
        };
        write_manifest(&temporary_directory.join(MANIFEST_FILE), &record)?;
        fs::rename(&temporary_directory, &destination_directory).context("无法原子提交模型导入")?;
        Ok(record)
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
    let mut buffer = [0_u8; 1024 * 1024];
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
        let source = temporary.path().join("voice.onnx");
        fs::write(&source, b"fake-onnx-for-library-test").unwrap();
        let library = ModelLibrary::open(temporary.path().join("models")).unwrap();

        let first = library
            .import_file(&source, Some("local-test".into()))
            .unwrap();
        let duplicate = library.import_file(&source, None).unwrap();
        assert_eq!(first.id, duplicate.id);
        assert_eq!(library.list().unwrap(), vec![first.clone()]);

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
}
