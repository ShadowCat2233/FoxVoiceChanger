use std::{cmp::Ordering, fs, path::Path};

use anyhow::{bail, Context, Result};

const MAGIC: &[u8; 8] = b"FVIX\0\0\0\x01";
const NEIGHBORS: usize = 8;

/// Compact IVF-flat index used by the realtime thread. Import tooling converts
/// FAISS files once; inference then has no Python or FAISS runtime dependency.
pub(super) struct FeatureIndex {
    dimension: usize,
    centroids: Vec<f32>,
    offsets: Vec<usize>,
    vectors: Vec<f32>,
    scratch: Vec<f32>,
}

impl FeatureIndex {
    pub(super) fn load(path: &Path, expected_dimension: i64) -> Result<Self> {
        let bytes = fs::read(path).with_context(|| format!("failed to read feature index {}", path.display()))?;
        if bytes.len() < 24 || &bytes[..8] != MAGIC {
            bail!("unsupported feature index format (expected FoxVoice FVIX v1)");
        }
        let dimension = read_u32(&bytes, 8)? as usize;
        let list_count = read_u32(&bytes, 12)? as usize;
        let vector_count = read_u64(&bytes, 16)? as usize;
        if dimension == 0 || list_count == 0 || vector_count == 0 {
            bail!("feature index dimensions must be non-zero");
        }
        let expected_dimension = usize::try_from(expected_dimension).context("invalid model feature dimension")?;
        if dimension != expected_dimension {
            bail!("feature index dimension {dimension} does not match model dimension {expected_dimension}");
        }
        let centroid_values = dimension.checked_mul(list_count).context("index size overflow")?;
        let offsets_start = 24 + centroid_values.checked_mul(4).context("index size overflow")?;
        let vectors_start = offsets_start + (list_count + 1).checked_mul(8).context("index size overflow")?;
        let expected = vectors_start + vector_count.checked_mul(dimension).and_then(|n| n.checked_mul(4)).context("index size overflow")?;
        if bytes.len() != expected { bail!("feature index length mismatch"); }
        let centroids = read_f32s(&bytes[24..offsets_start]);
        let mut offsets = Vec::with_capacity(list_count + 1);
        for i in 0..=list_count { offsets.push(read_u64(&bytes, offsets_start + i * 8)? as usize); }
        if offsets[0] != 0 || offsets[list_count] != vector_count || offsets.windows(2).any(|v| v[0] > v[1]) {
            bail!("feature index has invalid list offsets");
        }
        Ok(Self { dimension, centroids, offsets, vectors: read_f32s(&bytes[vectors_start..]), scratch: vec![0.0; dimension] })
    }

    pub(super) fn retrieve_tensor(&mut self, data: &[f32], shape: &[i64], output: &mut Vec<f32>) -> Result<()> {
        if shape.len() != 3 || shape[0] != 1 || usize::try_from(shape[2]).ok() != Some(self.dimension) {
            bail!("feature index dimension {} does not match content features", self.dimension);
        }
        output.clear();
        output.reserve(data.len());
        for frame in data.chunks_exact(self.dimension) {
            self.retrieve(frame);
            output.extend_from_slice(&self.scratch);
        }
        Ok(())
    }

    fn retrieve(&mut self, query: &[f32]) {
        let mut best_list = 0;
        let mut best_distance = f32::INFINITY;
        for (list, centroid) in self.centroids.chunks_exact(self.dimension).enumerate() {
            let distance = l2(query, centroid);
            if distance < best_distance { best_distance = distance; best_list = list; }
        }
        let start = self.offsets[best_list];
        let end = self.offsets[best_list + 1];
        if start == end {
            self.scratch.copy_from_slice(query);
            return;
        }
        let mut nearest: Vec<(f32, usize)> = (start..end).map(|i| (l2(query, &self.vectors[i*self.dimension..(i+1)*self.dimension]), i)).collect();
        let keep = NEIGHBORS.min(nearest.len());
        nearest.select_nth_unstable_by(keep - 1, |a, b| a.0.partial_cmp(&b.0).unwrap_or(Ordering::Equal));
        nearest.truncate(keep);
        self.scratch.fill(0.0);
        let weight_sum: f32 = nearest.iter().map(|(d, _)| 1.0 / d.max(1e-6).powi(2)).sum();
        for (distance, index) in nearest {
            let weight = (1.0 / distance.max(1e-6).powi(2)) / weight_sum;
            for (out, value) in self.scratch.iter_mut().zip(&self.vectors[index*self.dimension..(index+1)*self.dimension]) { *out += value * weight; }
        }
    }
}

fn l2(a: &[f32], b: &[f32]) -> f32 { a.iter().zip(b).map(|(x, y)| { let d=x-y; d*d }).sum() }
fn read_u32(bytes: &[u8], at: usize) -> Result<u32> { Ok(u32::from_le_bytes(bytes.get(at..at+4).context("truncated feature index")?.try_into().unwrap())) }
fn read_u64(bytes: &[u8], at: usize) -> Result<u64> { Ok(u64::from_le_bytes(bytes.get(at..at+8).context("truncated feature index")?.try_into().unwrap())) }
fn read_f32s(bytes: &[u8]) -> Vec<f32> { bytes.chunks_exact(4).map(|v| f32::from_le_bytes(v.try_into().unwrap())).collect() }

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_retrieves_and_rejects_dimension_mismatch() {
        let path = std::env::temp_dir().join(format!("foxvoice-feature-index-{}.fvix", std::process::id()));
        let mut bytes = MAGIC.to_vec();
        bytes.extend_from_slice(&2u32.to_le_bytes());
        bytes.extend_from_slice(&2u32.to_le_bytes());
        bytes.extend_from_slice(&3u64.to_le_bytes());
        for value in [0.0f32, 0.0, 10.0, 10.0] { bytes.extend_from_slice(&value.to_le_bytes()); }
        for offset in [0u64, 2, 3] { bytes.extend_from_slice(&offset.to_le_bytes()); }
        for value in [1.0f32, 1.0, 2.0, 2.0, 9.0, 9.0] { bytes.extend_from_slice(&value.to_le_bytes()); }
        fs::write(&path, bytes).unwrap();
        let mut index = FeatureIndex::load(&path, 2).unwrap();
        let mut output = Vec::new();
        index.retrieve_tensor(&[1.0, 1.0, 9.0, 9.0], &[1, 2, 2], &mut output).unwrap();
        assert_eq!(output.len(), 4);
        assert!(output[0] < 1.01 && output[0] > 0.99);
        assert_eq!(&output[2..], &[9.0, 9.0]);
        assert!(FeatureIndex::load(&path, 768).is_err());
        fs::remove_file(path).unwrap();
    }
}
