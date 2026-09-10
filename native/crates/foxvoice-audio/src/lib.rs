mod buffer;

pub use buffer::{AudioBufferConsumer, AudioBufferMetrics, AudioBufferProducer, audio_ring_buffer};

#[cfg(feature = "wasapi")]
mod wasapi {
    use std::sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    };

    use anyhow::{Context, Result, bail};
    use cpal::{
        Device, SampleFormat, Stream, StreamConfig, SupportedStreamConfig,
        SupportedStreamConfigRange,
        traits::{DeviceTrait, HostTrait, StreamTrait},
    };
    use foxvoice_contracts::{AudioDeviceDirection, AudioDeviceInfo};
    use rtrb::RingBuffer;

    const PREFERRED_SAMPLE_RATES: [u32; 2] = [48_000, 44_100];

    #[derive(Debug, Default)]
    struct MetricsInner {
        input_overruns: AtomicU64,
        output_underruns: AtomicU64,
        stream_errors: AtomicU64,
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct AudioMetricsSnapshot {
        pub input_overruns: u64,
        pub output_underruns: u64,
        pub stream_errors: u64,
    }

    #[derive(Debug, Clone)]
    pub struct AudioMetrics(Arc<MetricsInner>);

    impl AudioMetrics {
        pub fn snapshot(&self) -> AudioMetricsSnapshot {
            AudioMetricsSnapshot {
                input_overruns: self.0.input_overruns.load(Ordering::Relaxed),
                output_underruns: self.0.output_underruns.load(Ordering::Relaxed),
                stream_errors: self.0.stream_errors.load(Ordering::Relaxed),
            }
        }
    }

    pub struct RunningBypass {
        _input_stream: Stream,
        _output_stream: Stream,
        metrics: AudioMetrics,
        pub sample_rate: u32,
        pub input_channels: u16,
        pub output_channels: u16,
        pub buffer_ms: u32,
    }

    impl RunningBypass {
        pub fn metrics(&self) -> AudioMetricsSnapshot {
            self.metrics.snapshot()
        }
    }

    pub fn enumerate_devices() -> Result<Vec<AudioDeviceInfo>> {
        let host = cpal::default_host();
        let default_input = host
            .default_input_device()
            .and_then(|device| device.id().ok())
            .map(|id| id.to_string());
        let default_output = host
            .default_output_device()
            .and_then(|device| device.id().ok())
            .map(|id| id.to_string());
        let mut result = Vec::new();

        for device in host.input_devices().context("无法枚举 WASAPI 输入设备")? {
            result.push(device_info(
                &device,
                AudioDeviceDirection::Input,
                default_input.as_deref(),
            ));
        }
        for device in host.output_devices().context("无法枚举 WASAPI 输出设备")? {
            result.push(device_info(
                &device,
                AudioDeviceDirection::Output,
                default_output.as_deref(),
            ));
        }
        Ok(result)
    }

    pub fn start_safe_bypass(
        input_device_id: Option<&str>,
        output_device_id: Option<&str>,
        buffer_ms: u32,
    ) -> Result<RunningBypass> {
        anyhow::ensure!(
            (20..=500).contains(&buffer_ms),
            "安全旁路缓冲必须在 20–500 ms 之间"
        );
        let host = cpal::default_host();
        let input = select_device(&host, input_device_id, AudioDeviceDirection::Input)?;
        let output = select_device(&host, output_device_id, AudioDeviceDirection::Output)?;
        let (input_supported, output_supported, sample_rate) = common_f32_configs(&input, &output)?;
        let input_config: StreamConfig = input_supported.into();
        let output_config: StreamConfig = output_supported.into();
        let input_channels = input_config.channels as usize;
        let output_channels = output_config.channels as usize;
        let capacity = (sample_rate as usize * buffer_ms as usize / 1_000).max(256);
        let initial_delay = capacity / 2;
        let (mut producer, mut consumer) = RingBuffer::<f32>::new(capacity);
        for _ in 0..initial_delay {
            producer
                .push(0.0)
                .expect("new ring buffer has enough capacity");
        }

        let metrics = AudioMetrics(Arc::new(MetricsInner::default()));
        let input_metrics = Arc::clone(&metrics.0);
        let input_error_metrics = Arc::clone(&metrics.0);
        let output_metrics = Arc::clone(&metrics.0);
        let output_error_metrics = Arc::clone(&metrics.0);

        let input_stream = input
            .build_input_stream::<f32, _, _>(
                input_config,
                move |data, _| {
                    for frame in data.chunks(input_channels) {
                        let mono = frame.iter().copied().sum::<f32>() / input_channels as f32;
                        if producer.push(mono).is_err() {
                            input_metrics.input_overruns.fetch_add(1, Ordering::Relaxed);
                        }
                    }
                },
                move |_| {
                    input_error_metrics
                        .stream_errors
                        .fetch_add(1, Ordering::Relaxed);
                },
                None,
            )
            .context("无法创建 WASAPI 输入流")?;

        let output_stream = output
            .build_output_stream::<f32, _, _>(
                output_config,
                move |data, _| {
                    for frame in data.chunks_mut(output_channels) {
                        let sample = match consumer.pop() {
                            Ok(value) => value,
                            Err(_) => {
                                output_metrics
                                    .output_underruns
                                    .fetch_add(1, Ordering::Relaxed);
                                0.0
                            }
                        };
                        frame.fill(sample.clamp(-1.0, 1.0));
                    }
                },
                move |_| {
                    output_error_metrics
                        .stream_errors
                        .fetch_add(1, Ordering::Relaxed);
                },
                None,
            )
            .context("无法创建 WASAPI 输出流")?;

        output_stream.play().context("无法启动 WASAPI 输出流")?;
        input_stream.play().context("无法启动 WASAPI 输入流")?;

        Ok(RunningBypass {
            _input_stream: input_stream,
            _output_stream: output_stream,
            metrics,
            sample_rate,
            input_channels: input_channels as u16,
            output_channels: output_channels as u16,
            buffer_ms,
        })
    }

    fn device_info(
        device: &Device,
        direction: AudioDeviceDirection,
        default_id: Option<&str>,
    ) -> AudioDeviceInfo {
        let id = device
            .id()
            .map(|id| id.to_string())
            .unwrap_or_else(|_| device.to_string());
        let name = device
            .description()
            .map(|description| description.name().to_owned())
            .unwrap_or_else(|_| device.to_string());
        let config = match direction {
            AudioDeviceDirection::Input => device.default_input_config(),
            AudioDeviceDirection::Output => device.default_output_config(),
        }
        .ok();
        AudioDeviceInfo {
            is_default: default_id == Some(id.as_str()),
            id,
            name,
            direction,
            channels: config.map(|value| value.channels()),
            sample_rate: config.map(|value| value.sample_rate()),
            sample_format: config.map(|value| value.sample_format().to_string()),
        }
    }

    fn select_device(
        host: &cpal::Host,
        requested_id: Option<&str>,
        direction: AudioDeviceDirection,
    ) -> Result<Device> {
        if let Some(requested_id) = requested_id {
            let mut devices = match direction {
                AudioDeviceDirection::Input => host.input_devices(),
                AudioDeviceDirection::Output => host.output_devices(),
            }
            .context("无法读取 WASAPI 设备列表")?;
            return devices
                .find(|device| device.id().is_ok_and(|id| id.to_string() == requested_id))
                .with_context(|| format!("指定的音频设备不存在: {requested_id}"));
        }
        match direction {
            AudioDeviceDirection::Input => host.default_input_device(),
            AudioDeviceDirection::Output => host.default_output_device(),
        }
        .context("系统没有可用的默认音频设备")
    }

    fn common_f32_configs(
        input: &Device,
        output: &Device,
    ) -> Result<(SupportedStreamConfig, SupportedStreamConfig, u32)> {
        let inputs: Vec<_> = input
            .supported_input_configs()
            .context("无法读取输入设备格式")?
            .filter(|config| config.sample_format() == SampleFormat::F32)
            .collect();
        let outputs: Vec<_> = output
            .supported_output_configs()
            .context("无法读取输出设备格式")?
            .filter(|config| config.sample_format() == SampleFormat::F32)
            .collect();

        for rate in PREFERRED_SAMPLE_RATES {
            if let Some(pair) = configs_at_rate(&inputs, &outputs, rate) {
                return Ok(pair);
            }
        }
        for input_config in &inputs {
            for output_config in &outputs {
                let min_rate = input_config
                    .min_sample_rate()
                    .max(output_config.min_sample_rate());
                let max_rate = input_config
                    .max_sample_rate()
                    .min(output_config.max_sample_rate());
                if min_rate <= max_rate {
                    return Ok((
                        input_config.with_sample_rate(max_rate),
                        output_config.with_sample_rate(max_rate),
                        max_rate,
                    ));
                }
            }
        }
        bail!("输入和输出设备没有共同的 F32 采样率；重采样器接入前无法安全旁路")
    }

    fn configs_at_rate(
        inputs: &[SupportedStreamConfigRange],
        outputs: &[SupportedStreamConfigRange],
        rate: u32,
    ) -> Option<(SupportedStreamConfig, SupportedStreamConfig, u32)> {
        let input = inputs.iter().find(|config| config.contains_rate(rate))?;
        let output = outputs.iter().find(|config| config.contains_rate(rate))?;
        Some((
            input.with_sample_rate(rate),
            output.with_sample_rate(rate),
            rate,
        ))
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn metrics_snapshot_starts_at_zero() {
            let metrics = AudioMetrics(Arc::new(MetricsInner::default()));
            assert_eq!(
                metrics.snapshot(),
                AudioMetricsSnapshot {
                    input_overruns: 0,
                    output_underruns: 0,
                    stream_errors: 0
                }
            );
        }

        #[test]
        fn configured_buffer_limits_are_explicit() {
            assert!(!(20..=500).contains(&19));
            assert!((20..=500).contains(&120));
            assert!(!(20..=500).contains(&501));
        }
    }
}

#[cfg(feature = "wasapi")]
pub use wasapi::{
    AudioMetrics, AudioMetricsSnapshot, RunningBypass, enumerate_devices, start_safe_bypass,
};
