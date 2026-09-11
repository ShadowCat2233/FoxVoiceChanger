//! Minimal ONNX protobuf profile reader for RVC metadata.
//!
//! The wire-field selection follows vc-rs' MIT-licensed `onnx_meta` reader at
//! the pinned revision used by FoxVoice. Only graph inputs and metadata_props
//! are decoded; tensor weights and nodes are skipped.

use std::{fs, path::Path};

use anyhow::{Context, Result, bail};

#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct OnnxProfile {
    pub rvc_version: Option<String>,
    pub sample_rate: Option<u32>,
    pub uses_f0: Option<bool>,
    pub streaming: bool,
}

#[derive(Debug, Default)]
struct ModelInfo {
    inputs: Vec<TensorInfo>,
    metadata: Vec<(String, String)>,
}

#[derive(Debug)]
struct TensorInfo {
    name: String,
    dims: Vec<i64>,
}

pub(crate) fn inspect(path: &Path) -> Result<OnnxProfile> {
    let bytes =
        fs::read(path).with_context(|| format!("无法读取 ONNX 元数据: {}", path.display()))?;
    let info = parse_model(&bytes).context("无法解析 ONNX protobuf 元数据")?;
    let feature_channels = info
        .inputs
        .iter()
        .find(|input| matches!(input.name.as_str(), "feats" | "phone"))
        .and_then(|input| input.dims.last().copied())
        .filter(|value| *value > 0);
    let rvc_version = metadata_value(&info, "rvc.model_version")
        .map(str::trim)
        .filter(|value| matches!(*value, "v1" | "v2"))
        .map(ToOwned::to_owned)
        .or_else(|| match feature_channels {
            Some(768) => Some("v2".into()),
            Some(256) => Some("v1".into()),
            _ => None,
        });
    let has_pitch = info.inputs.iter().any(|input| input.name == "pitch");
    let has_pitchf = info
        .inputs
        .iter()
        .any(|input| matches!(input.name.as_str(), "pitchf" | "nsff0"));
    let uses_f0 = (has_pitch && has_pitchf).then_some(true);
    let streaming = metadata_value(&info, "rvc.export_mode") == Some("streaming");
    let sample_rate = metadata_value(&info, "rvc.sample_rate")
        .and_then(parse_positive_u32)
        .or_else(|| metadata_value(&info, "metadata").and_then(sample_rate_from_json));
    Ok(OnnxProfile {
        rvc_version,
        sample_rate,
        uses_f0,
        streaming,
    })
}

fn metadata_value<'a>(info: &'a ModelInfo, key: &str) -> Option<&'a str> {
    info.metadata
        .iter()
        .find(|(name, _)| name == key)
        .map(|(_, value)| value.as_str())
}

fn parse_positive_u32(value: &str) -> Option<u32> {
    value.trim().parse().ok().filter(|value| *value > 0)
}

fn sample_rate_from_json(value: &str) -> Option<u32> {
    let json: serde_json::Value = serde_json::from_str(value).ok()?;
    json.get("samplingRate")
        .or_else(|| json.get("sampleRate"))
        .and_then(|value| value.as_u64().or_else(|| value.as_str()?.parse().ok()))
        .and_then(|value| u32::try_from(value).ok())
        .filter(|value| *value > 0)
}

struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> Reader<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }
    fn eof(&self) -> bool {
        self.position >= self.bytes.len()
    }
    fn varint(&mut self) -> Result<u64> {
        let mut value = 0_u64;
        let mut shift = 0_u32;
        loop {
            let byte = *self
                .bytes
                .get(self.position)
                .context("protobuf varint 超出文件")?;
            self.position += 1;
            if shift >= 64 {
                bail!("protobuf varint 超过 64 位");
            }
            value |= u64::from(byte & 0x7f) << shift;
            if byte & 0x80 == 0 {
                return Ok(value);
            }
            shift += 7;
        }
    }
    fn len_prefixed(&mut self) -> Result<&'a [u8]> {
        let length = usize::try_from(self.varint()?).context("protobuf 字段长度过大")?;
        let end = self
            .position
            .checked_add(length)
            .context("protobuf 字段长度溢出")?;
        let value = self
            .bytes
            .get(self.position..end)
            .context("protobuf 字段超出文件")?;
        self.position = end;
        Ok(value)
    }
    fn advance(&mut self, length: usize) -> Result<()> {
        let end = self
            .position
            .checked_add(length)
            .context("protobuf 跳过长度溢出")?;
        if end > self.bytes.len() {
            bail!("protobuf 定长字段超出文件");
        }
        self.position = end;
        Ok(())
    }
    fn skip(&mut self, wire: u64) -> Result<()> {
        match wire {
            0 => {
                self.varint()?;
            }
            1 => self.advance(8)?,
            2 => {
                self.len_prefixed()?;
            }
            5 => self.advance(4)?,
            _ => bail!("不支持的 protobuf wire type: {wire}"),
        }
        Ok(())
    }
}

fn fields(
    bytes: &[u8],
    mut visitor: impl FnMut(u64, u64, &mut Reader<'_>) -> Result<bool>,
) -> Result<()> {
    let mut reader = Reader::new(bytes);
    while !reader.eof() {
        let tag = reader.varint()?;
        let field = tag >> 3;
        let wire = tag & 7;
        if !visitor(field, wire, &mut reader)? {
            reader.skip(wire)?;
        }
    }
    Ok(())
}

fn parse_model(bytes: &[u8]) -> Result<ModelInfo> {
    let mut info = ModelInfo::default();
    fields(bytes, |field, wire, reader| match (field, wire) {
        (7, 2) => {
            parse_graph(reader.len_prefixed()?, &mut info)?;
            Ok(true)
        }
        (14, 2) => {
            if let Some(entry) = parse_string_entry(reader.len_prefixed()?)? {
                info.metadata.push(entry);
            }
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(info)
}

fn parse_graph(bytes: &[u8], info: &mut ModelInfo) -> Result<()> {
    fields(bytes, |field, wire, reader| match (field, wire) {
        (11, 2) => {
            info.inputs.push(parse_value_info(reader.len_prefixed()?)?);
            Ok(true)
        }
        _ => Ok(false),
    })
}

fn parse_value_info(bytes: &[u8]) -> Result<TensorInfo> {
    let mut name = String::new();
    let mut dims = Vec::new();
    fields(bytes, |field, wire, reader| match (field, wire) {
        (1, 2) => {
            name = read_utf8(reader.len_prefixed()?, "ONNX 输入名")?;
            Ok(true)
        }
        (2, 2) => {
            dims = parse_type(reader.len_prefixed()?)?;
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(TensorInfo { name, dims })
}

fn parse_type(bytes: &[u8]) -> Result<Vec<i64>> {
    let mut dims = Vec::new();
    fields(bytes, |field, wire, reader| match (field, wire) {
        (1, 2) => {
            dims = parse_tensor_type(reader.len_prefixed()?)?;
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(dims)
}

fn parse_tensor_type(bytes: &[u8]) -> Result<Vec<i64>> {
    let mut dims = Vec::new();
    fields(bytes, |field, wire, reader| match (field, wire) {
        (2, 2) => {
            dims = parse_shape(reader.len_prefixed()?)?;
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(dims)
}

fn parse_shape(bytes: &[u8]) -> Result<Vec<i64>> {
    let mut dims = Vec::new();
    fields(bytes, |field, wire, reader| match (field, wire) {
        (1, 2) => {
            dims.push(parse_dimension(reader.len_prefixed()?)?);
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(dims)
}

fn parse_dimension(bytes: &[u8]) -> Result<i64> {
    let mut value = 0_i64;
    fields(bytes, |field, wire, reader| match (field, wire) {
        (1, 0) => {
            value = reader.varint()? as i64;
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(value)
}

fn parse_string_entry(bytes: &[u8]) -> Result<Option<(String, String)>> {
    let mut key = None;
    let mut value = None;
    fields(bytes, |field, wire, reader| match (field, wire) {
        (1, 2) => {
            key = Some(read_utf8(reader.len_prefixed()?, "ONNX metadata key")?);
            Ok(true)
        }
        (2, 2) => {
            value = Some(read_utf8(reader.len_prefixed()?, "ONNX metadata value")?);
            Ok(true)
        }
        _ => Ok(false),
    })?;
    Ok(key.zip(value))
}

fn read_utf8(bytes: &[u8], label: &str) -> Result<String> {
    String::from_utf8(bytes.to_vec()).with_context(|| format!("{label} 不是 UTF-8"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn varint(mut value: u64, output: &mut Vec<u8>) {
        loop {
            let mut byte = (value & 0x7f) as u8;
            value >>= 7;
            if value != 0 {
                byte |= 0x80;
            }
            output.push(byte);
            if value == 0 {
                break;
            }
        }
    }
    fn field(number: u64, payload: &[u8], output: &mut Vec<u8>) {
        varint((number << 3) | 2, output);
        varint(payload.len() as u64, output);
        output.extend_from_slice(payload);
    }
    fn scalar(number: u64, value: u64, output: &mut Vec<u8>) {
        varint(number << 3, output);
        varint(value, output);
    }
    fn input(name: &str, dims: &[i64]) -> Vec<u8> {
        let mut shape = Vec::new();
        for dim in dims {
            let mut dimension = Vec::new();
            scalar(1, *dim as u64, &mut dimension);
            field(1, &dimension, &mut shape);
        }
        let mut tensor = Vec::new();
        scalar(1, 1, &mut tensor);
        field(2, &shape, &mut tensor);
        let mut kind = Vec::new();
        field(1, &tensor, &mut kind);
        let mut value = Vec::new();
        field(1, name.as_bytes(), &mut value);
        field(2, &kind, &mut value);
        value
    }
    fn metadata(key: &str, value: &str) -> Vec<u8> {
        let mut entry = Vec::new();
        field(1, key.as_bytes(), &mut entry);
        field(2, value.as_bytes(), &mut entry);
        entry
    }

    #[test]
    fn derives_rvc_v2_f0_and_sample_rate() {
        let mut graph = Vec::new();
        for (name, dims) in [
            ("phone", vec![1, 0, 256]),
            ("pitch", vec![1, 0]),
            ("nsff0", vec![1, 0]),
        ] {
            field(11, &input(name, &dims), &mut graph);
        }
        let mut model = Vec::new();
        field(7, &graph, &mut model);
        field(
            14,
            &metadata("metadata", r#"{"f0":true,"samplingRate":40000}"#),
            &mut model,
        );
        field(14, &metadata("rvc.model_version", "v2"), &mut model);
        let info = parse_model(&model).unwrap();
        let temporary = tempfile::NamedTempFile::new().unwrap();
        fs::write(temporary.path(), model).unwrap();
        assert_eq!(info.inputs.len(), 3);
        assert_eq!(
            inspect(temporary.path()).unwrap(),
            OnnxProfile {
                rvc_version: Some("v2".into()),
                sample_rate: Some(40_000),
                uses_f0: Some(true),
                streaming: false,
            }
        );
    }

    #[test]
    fn rejects_truncated_protobuf_without_guessing() {
        assert!(parse_model(&[0x3a, 0xff]).is_err());
    }
}
