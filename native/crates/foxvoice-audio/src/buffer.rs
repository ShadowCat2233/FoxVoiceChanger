use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};

use rtrb::{Consumer, Producer, RingBuffer};

#[derive(Debug, Default)]
struct MetricsInner {
    overruns: AtomicU64,
    underruns: AtomicU64,
}

#[derive(Debug, Clone)]
pub struct AudioBufferMetrics(Arc<MetricsInner>);

impl AudioBufferMetrics {
    pub fn overruns(&self) -> u64 {
        self.0.overruns.load(Ordering::Relaxed)
    }

    pub fn underruns(&self) -> u64 {
        self.0.underruns.load(Ordering::Relaxed)
    }
}

pub struct AudioBufferProducer {
    inner: Producer<f32>,
    metrics: AudioBufferMetrics,
}

impl AudioBufferProducer {
    pub fn push_mono(&mut self, sample: f32) {
        if self.inner.push(sample.clamp(-1.0, 1.0)).is_err() {
            self.metrics.0.overruns.fetch_add(1, Ordering::Relaxed);
        }
    }

    pub fn push_interleaved_as_mono(&mut self, input: &[f32], channels: usize) {
        if channels == 0 {
            return;
        }
        for frame in input.chunks(channels) {
            let mono = frame.iter().copied().sum::<f32>() / frame.len() as f32;
            self.push_mono(mono);
        }
    }

    pub fn prefill_silence(&mut self, samples: usize) {
        for _ in 0..samples {
            self.push_mono(0.0);
        }
    }
}

pub struct AudioBufferConsumer {
    inner: Consumer<f32>,
    metrics: AudioBufferMetrics,
}

impl AudioBufferConsumer {
    pub fn pop_mono_or_silence(&mut self) -> f32 {
        self.inner.pop().unwrap_or_else(|_| {
            self.metrics.0.underruns.fetch_add(1, Ordering::Relaxed);
            0.0
        })
    }

    pub fn fill_interleaved(&mut self, output: &mut [f32], channels: usize) {
        if channels == 0 {
            output.fill(0.0);
            return;
        }
        for frame in output.chunks_mut(channels) {
            frame.fill(self.pop_mono_or_silence());
        }
    }
}

pub fn audio_ring_buffer(
    capacity_samples: usize,
) -> (AudioBufferProducer, AudioBufferConsumer, AudioBufferMetrics) {
    assert!(capacity_samples > 0, "audio ring capacity must be non-zero");
    let (producer, consumer) = RingBuffer::new(capacity_samples);
    let metrics = AudioBufferMetrics(Arc::new(MetricsInner::default()));
    (
        AudioBufferProducer {
            inner: producer,
            metrics: metrics.clone(),
        },
        AudioBufferConsumer {
            inner: consumer,
            metrics: metrics.clone(),
        },
        metrics,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converts_stereo_input_to_mono_and_repeats_to_output() {
        let (mut producer, mut consumer, metrics) = audio_ring_buffer(4);
        producer.push_interleaved_as_mono(&[0.25, 0.75, -0.5, 0.5], 2);
        let mut output = [0.0; 4];
        consumer.fill_interleaved(&mut output, 2);
        assert_eq!(output, [0.5, 0.5, 0.0, 0.0]);
        assert_eq!(metrics.overruns(), 0);
        assert_eq!(metrics.underruns(), 0);
    }

    #[test]
    fn overflow_drops_new_sample_and_counts_it() {
        let (mut producer, _consumer, metrics) = audio_ring_buffer(1);
        producer.push_mono(0.25);
        producer.push_mono(0.5);
        assert_eq!(metrics.overruns(), 1);
    }

    #[test]
    fn underflow_returns_silence_and_counts_it() {
        let (_producer, mut consumer, metrics) = audio_ring_buffer(1);
        assert_eq!(consumer.pop_mono_or_silence(), 0.0);
        assert_eq!(metrics.underruns(), 1);
    }
}
