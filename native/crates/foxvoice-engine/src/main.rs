use std::{
    env,
    io::{self, BufRead},
    path::PathBuf,
    sync::mpsc,
    thread,
    time::{Duration, Instant},
};

use anyhow::{Context, Result, bail};
use cpal::{
    SampleFormat, StreamConfig,
    traits::{DeviceTrait, HostTrait, StreamTrait},
};
use serde::Deserialize;
use serde_json::json;
use vc_app::{AudioHost, DenoiserMode, EngineController, EngineState, LiveParams, RealtimeConfig};
use vc_core::{
    Provider,
    model_rvc::{
        F0Config, NoiseGateShaping, OutputDynamicsConfig, RvcPipeline, RvcPipelineConfig,
        VoiceModel,
    },
    validation::RvcChunkTiming,
};

fn main() {
    if let Err(error) = run() {
        eprintln!("foxvoice-engine: {error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    match env::args().nth(1).as_deref().unwrap_or("devices") {
        "devices" => list_devices(),
        "passthrough" => run_engine(true),
        "rvc" => run_engine(false),
        "validate-rvc" => validate_rvc(),
        "provider-status" => provider_status(),
        "convert-wav" => convert_wav(),
        "play-audio" | "play-wav" => play_audio(),
        "inspect-audio" => inspect_audio(),
        _ => bail!(
            "可用命令: devices, passthrough, rvc, validate-rvc, provider-status, convert-wav, play-audio, inspect-audio"
        ),
    }
}

fn inspect_audio() -> Result<()> {
    let arguments: Vec<String> = env::args().collect();
    let path = required_path(&arguments, "--file")?;
    let (samples, sample_rate) = read_audio_mono(&path)?;
    println!(
        "{}",
        json!({
            "ok": true,
            "file": path,
            "sampleRate": sample_rate,
            "samples": samples.len(),
            "durationMs": samples.len() as f64 * 1000.0 / sample_rate as f64
        })
    );
    Ok(())
}

fn convert_wav() -> Result<()> {
    let arguments: Vec<String> = env::args().collect();
    let input = required_path(&arguments, "--input")?;
    let output =
        PathBuf::from(option_value(&arguments, "--output").context("缺少参数 --output <path>")?);
    anyhow::ensure!(input != output, "输出文件不能覆盖输入文件");
    anyhow::ensure!(
        output
            .extension()
            .and_then(|value| value.to_str())
            .is_some_and(|value| value.eq_ignore_ascii_case("wav")),
        "离线转换输出必须是 .wav 文件"
    );
    if let Some(parent) = output.parent() {
        anyhow::ensure!(parent.is_dir(), "输出目录不存在: {}", parent.display());
    }
    let model = required_path(&arguments, "--model")?;
    let embedder = required_path(&arguments, "--embedder")?;
    let f0_model = required_path(&arguments, "--f0")?;
    let pitch_shift = option_value(&arguments, "--pitch")
        .map(str::parse)
        .transpose()
        .context("--pitch 必须是数字")?
        .unwrap_or(0.0_f32);
    let provider = selected_provider(&arguments)?;
    anyhow::ensure!(
        (-24.0..=24.0).contains(&pitch_shift),
        "--pitch 必须在 -24 到 +24 半音之间"
    );
    let (source, source_rate) = read_wav_mono(&input)?;
    anyhow::ensure!(!source.is_empty(), "输入 WAV 没有音频采样");
    let worker = thread::Builder::new()
        .name("foxvoice-offline-convert".into())
        .stack_size(64 * 1024 * 1024)
        .spawn(move || {
            convert_wav_inner(
                source,
                source_rate,
                model,
                embedder,
                f0_model,
                output,
                (pitch_shift, provider),
            )
        })
        .context("无法启动离线转换线程")?;
    let report = worker
        .join()
        .map_err(|_| anyhow::anyhow!("离线转换线程异常终止"))??;
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
}

fn convert_wav_inner(
    source: Vec<f32>,
    source_rate: u32,
    model: PathBuf,
    embedder: PathBuf,
    f0_model: PathBuf,
    output: PathBuf,
    runtime: (f32, Provider),
) -> Result<serde_json::Value> {
    let (pitch_shift, provider) = runtime;
    let sample_rate = 48_000;
    let timing = RvcChunkTiming::from_ms(160, sample_rate)?;
    let input = resample_linear(&source, source_rate, sample_rate);
    let started = Instant::now();
    let mut pipeline = RvcPipeline::load(RvcPipelineConfig {
        model: &model,
        embedder: &embedder,
        embedder_output: None,
        f0_model: &f0_model,
        provider,
        gpu_priority: vc_core::model_rvc::GpuPriority::Normal,
        gpu_device_id: 0,
        sample_rate,
        chunk_samples: timing.input_chunk_samples,
        speaker_id: 0,
        pitch_shift,
        f0: F0Config::default(),
        input_gain: 1.0,
        noise_gate_enabled: false,
        noise_gate_threshold: 0.01,
        noise_gate_shaping: NoiseGateShaping::default(),
        output_extra_ms: 60,
        volume_excluded_ms: 40,
        extra_convert_ms: 80,
        output_gain: 1.0,
        output_dynamics: OutputDynamicsConfig::default(),
        progress: None,
    })?;
    let mut converted = Vec::new();
    let mut chunk_audio = Vec::new();
    let mut pitch = Vec::new();
    let mut output_rate = sample_rate;
    for (index, chunk) in input.chunks(timing.input_chunk_samples).enumerate() {
        let mut padded = vec![0.0_f32; timing.input_chunk_samples];
        padded[..chunk.len()].copy_from_slice(chunk);
        let result = pipeline.process(&padded, sample_rate, &mut chunk_audio, &mut pitch)?;
        output_rate = result.sample_rate;
        let keep = if chunk.len() == timing.input_chunk_samples {
            chunk_audio.len()
        } else {
            chunk_audio.len().saturating_mul(chunk.len()) / timing.input_chunk_samples
        };
        converted.extend_from_slice(&chunk_audio[..keep]);
        if index % 10 == 0 {
            eprintln!(
                "FOXVOICE_PROGRESS={}",
                json!({"chunks": index + 1, "totalChunks": input.len().div_ceil(timing.input_chunk_samples)})
            );
        }
    }
    let temporary = output.with_extension("wav.partial");
    let spec = hound::WavSpec {
        channels: 1,
        sample_rate: output_rate,
        bits_per_sample: 16,
        sample_format: hound::SampleFormat::Int,
    };
    let mut writer = hound::WavWriter::create(&temporary, spec).context("无法创建离线输出 WAV")?;
    for sample in &converted {
        writer.write_sample((sample.clamp(-1.0, 1.0) * i16::MAX as f32) as i16)?;
    }
    writer.finalize()?;
    if output.exists() {
        std::fs::remove_file(&output).context("无法替换现有输出文件")?;
    }
    std::fs::rename(&temporary, &output).context("无法提交离线输出文件")?;
    Ok(json!({
        "ok": true, "output": output, "sampleRate": output_rate,
        "samples": converted.len(), "elapsedMs": started.elapsed().as_secs_f64() * 1000.0
    }))
}

fn play_audio() -> Result<()> {
    use std::sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    };

    let arguments: Vec<String> = env::args().collect();
    let path = required_path(&arguments, "--file")?;
    let gain_db = option_value(&arguments, "--gain-db")
        .map(str::parse)
        .transpose()
        .context("--gain-db 必须是数字")?
        .unwrap_or(0.0_f32);
    anyhow::ensure!(
        (-36.0..=12.0).contains(&gain_db),
        "--gain-db 必须在 -36 到 +12 dB 之间"
    );
    let (source, source_rate) = read_audio_mono(&path)?;
    let host = cpal::default_host();
    let requested = option_value(&arguments, "--output");
    let device = if let Some(name) = requested {
        host.output_devices()
            .context("无法枚举音效输出设备")?
            .find(|device| {
                device
                    .description()
                    .is_ok_and(|value| value.name().contains(name))
            })
            .with_context(|| format!("音效输出设备不存在: {name}"))?
    } else {
        host.default_output_device()
            .context("系统没有默认音效输出设备")?
    };
    let supported = device
        .default_output_config()
        .context("无法读取音效输出格式")?;
    let sample_rate = supported.sample_rate();
    let channels = usize::from(supported.channels());
    let gain = db_to_gain(gain_db);
    let mut prepared = resample_linear(&source, source_rate, sample_rate);
    apply_soundboard_dsp(&mut prepared, gain, sample_rate);
    let samples = Arc::new(prepared);
    let position = Arc::new(AtomicUsize::new(0));
    let failed = Arc::new(AtomicBool::new(false));
    let config: StreamConfig = supported.into();
    let stream = match supported.sample_format() {
        SampleFormat::F32 => build_sound_stream::<f32>(
            &device,
            &config,
            channels,
            Arc::clone(&samples),
            Arc::clone(&position),
            Arc::clone(&failed),
            |v| v,
        )?,
        SampleFormat::I16 => build_sound_stream::<i16>(
            &device,
            &config,
            channels,
            Arc::clone(&samples),
            Arc::clone(&position),
            Arc::clone(&failed),
            |v| (v * i16::MAX as f32) as i16,
        )?,
        SampleFormat::U16 => build_sound_stream::<u16>(
            &device,
            &config,
            channels,
            Arc::clone(&samples),
            Arc::clone(&position),
            Arc::clone(&failed),
            |v| ((v * 0.5 + 0.5) * u16::MAX as f32) as u16,
        )?,
        other => bail!("当前音效板不支持输出格式 {other}"),
    };
    stream.play().context("无法启动音效输出")?;
    while position.load(Ordering::Relaxed) < samples.len() && !failed.load(Ordering::Relaxed) {
        thread::sleep(Duration::from_millis(10));
    }
    anyhow::ensure!(!failed.load(Ordering::Relaxed), "音效播放期间设备发生错误");
    println!(
        "{}",
        json!({"ok": true, "file": path, "sampleRate": sample_rate, "samples": samples.len()})
    );
    Ok(())
}

fn read_audio_mono(path: &std::path::Path) -> Result<(Vec<f32>, u32)> {
    anyhow::ensure!(
        path.metadata().context("无法读取音效文件信息")?.len() <= 512 * 1024 * 1024,
        "音效文件超过 512 MiB 上限"
    );
    if path
        .extension()
        .and_then(|value| value.to_str())
        .is_some_and(|value| value.eq_ignore_ascii_case("wav"))
    {
        return read_wav_mono(path);
    }
    use symphonia::core::{
        audio::SampleBuffer, codecs::DecoderOptions, errors::Error as SymphoniaError,
        formats::FormatOptions, io::MediaSourceStream, meta::MetadataOptions, probe::Hint,
    };

    let extension = path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or("");
    anyhow::ensure!(
        matches!(
            extension.to_ascii_lowercase().as_str(),
            "flac" | "mp3" | "ogg"
        ),
        "音效板仅支持 WAV、FLAC、MP3 和 OGG"
    );
    let file = std::fs::File::open(path).context("无法打开音效文件")?;
    let mut hint = Hint::new();
    hint.with_extension(extension);
    let probed = symphonia::default::get_probe()
        .format(
            &hint,
            MediaSourceStream::new(Box::new(file), Default::default()),
            &FormatOptions::default(),
            &MetadataOptions::default(),
        )
        .context("无法识别音效格式")?;
    let mut format = probed.format;
    let track = format.default_track().context("音效文件没有默认音轨")?;
    let track_id = track.id;
    let mut decoder = symphonia::default::get_codecs()
        .make(&track.codec_params, &DecoderOptions::default())
        .context("无法创建音效解码器")?;
    let mut mono = Vec::new();
    let mut sample_rate = 0;
    loop {
        let packet = match format.next_packet() {
            Ok(packet) => packet,
            Err(SymphoniaError::IoError(error))
                if error.kind() == std::io::ErrorKind::UnexpectedEof =>
            {
                break;
            }
            Err(error) => return Err(error).context("读取音效数据失败"),
        };
        if packet.track_id() != track_id {
            continue;
        }
        let decoded = match decoder.decode(&packet) {
            Ok(decoded) => decoded,
            Err(SymphoniaError::DecodeError(_)) => continue,
            Err(error) => return Err(error).context("解码音效失败"),
        };
        let spec = *decoded.spec();
        sample_rate = spec.rate;
        let channels = spec.channels.count();
        anyhow::ensure!(channels > 0 && sample_rate > 0, "音效音频格式无效");
        let mut buffer = SampleBuffer::<f32>::new(decoded.capacity() as u64, spec);
        buffer.copy_interleaved_ref(decoded);
        mono.extend(
            buffer
                .samples()
                .chunks(channels)
                .map(|frame| frame.iter().copied().sum::<f32>() / channels as f32),
        );
        anyhow::ensure!(
            mono.len() <= sample_rate as usize * 60 * 30,
            "音效时长超过 30 分钟上限"
        );
    }
    anyhow::ensure!(
        !mono.is_empty() && sample_rate > 0,
        "音效文件没有可解码采样"
    );
    Ok((mono, sample_rate))
}

fn apply_soundboard_dsp(samples: &mut [f32], gain: f32, sample_rate: u32) {
    let fade_samples = ((sample_rate as usize * 5) / 1000).min(samples.len() / 2);
    let length = samples.len();
    for (index, sample) in samples.iter_mut().enumerate() {
        let fade_in = if fade_samples == 0 {
            1.0
        } else {
            (index + 1).min(fade_samples) as f32 / fade_samples as f32
        };
        let remaining = length - index;
        let fade_out = if fade_samples == 0 {
            1.0
        } else {
            remaining.min(fade_samples) as f32 / fade_samples as f32
        };
        *sample = (*sample * gain * fade_in.min(fade_out)).clamp(-1.0, 1.0);
    }
}

fn build_sound_stream<T: cpal::SizedSample + Send + 'static>(
    device: &cpal::Device,
    config: &StreamConfig,
    channels: usize,
    samples: std::sync::Arc<Vec<f32>>,
    position: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    failed: std::sync::Arc<std::sync::atomic::AtomicBool>,
    convert: fn(f32) -> T,
) -> Result<cpal::Stream> {
    use std::sync::atomic::Ordering;
    let error_flag = std::sync::Arc::clone(&failed);
    device
        .build_output_stream::<T, _, _>(
            *config,
            move |output, _| {
                for frame in output.chunks_mut(channels) {
                    let index = position.fetch_add(1, Ordering::Relaxed);
                    let value = samples.get(index).copied().unwrap_or(0.0);
                    frame.fill_with(|| convert(value));
                }
            },
            move |_| {
                error_flag.store(true, Ordering::Relaxed);
            },
            None,
        )
        .context("无法创建音效输出流")
}

fn read_wav_mono(path: &std::path::Path) -> Result<(Vec<f32>, u32)> {
    let mut reader = hound::WavReader::open(path).context("无法读取 WAV 文件")?;
    let spec = reader.spec();
    anyhow::ensure!(
        spec.channels > 0 && spec.sample_rate > 0,
        "WAV 音频格式无效"
    );
    let interleaved: Vec<f32> = match (spec.sample_format, spec.bits_per_sample) {
        (hound::SampleFormat::Float, 32) => reader.samples::<f32>().collect::<Result<_, _>>()?,
        (hound::SampleFormat::Int, bits) if bits <= 16 => reader
            .samples::<i16>()
            .map(|value| value.map(|sample| sample as f32 / i16::MAX as f32))
            .collect::<Result<_, _>>()?,
        (hound::SampleFormat::Int, bits) if bits <= 32 => {
            let scale = ((1_i64 << (bits - 1)) - 1) as f32;
            reader
                .samples::<i32>()
                .map(|value| value.map(|sample| sample as f32 / scale))
                .collect::<Result<_, _>>()?
        }
        _ => bail!("不支持此 WAV 位深"),
    };
    let channels = usize::from(spec.channels);
    let mono = interleaved
        .chunks(channels)
        .map(|frame| frame.iter().copied().sum::<f32>() / frame.len() as f32)
        .collect();
    Ok((mono, spec.sample_rate))
}

fn resample_linear(input: &[f32], source_rate: u32, target_rate: u32) -> Vec<f32> {
    if input.is_empty() || source_rate == target_rate {
        return input.to_vec();
    }
    let output_len = ((input.len() as u64 * target_rate as u64) / source_rate as u64) as usize;
    (0..output_len)
        .map(|index| {
            let source = index as f64 * source_rate as f64 / target_rate as f64;
            let left = source.floor() as usize;
            let right = (left + 1).min(input.len() - 1);
            let fraction = (source - left as f64) as f32;
            input[left] + (input[right] - input[left]) * fraction
        })
        .collect()
}

fn validate_rvc() -> Result<()> {
    let arguments: Vec<String> = env::args().collect();
    let model = required_path(&arguments, "--model")?;
    let embedder = required_path(&arguments, "--embedder")?;
    let f0_model = required_path(&arguments, "--f0")?;
    let provider = selected_provider(&arguments)?;
    let provider_label = provider.label();
    let frames = option_value(&arguments, "--frames")
        .map(str::parse)
        .transpose()
        .context("--frames 必须是整数")?
        .unwrap_or(1_usize);
    anyhow::ensure!(
        (1..=1000).contains(&frames),
        "--frames 必须在 1 到 1000 之间"
    );
    let worker = thread::Builder::new()
        .name("foxvoice-rvc-self-test".into())
        .stack_size(64 * 1024 * 1024)
        .spawn(move || -> Result<serde_json::Value> {
            let sample_rate = 48_000;
            let timing = RvcChunkTiming::from_ms(160, sample_rate)?;
            let load_started = Instant::now();
            let mut pipeline = RvcPipeline::load(RvcPipelineConfig {
                model: &model,
                embedder: &embedder,
                embedder_output: None,
                f0_model: &f0_model,
                provider,
                gpu_priority: vc_core::model_rvc::GpuPriority::High,
                gpu_device_id: 0,
                sample_rate,
                chunk_samples: timing.input_chunk_samples,
                speaker_id: 0,
                pitch_shift: 0.0,
                f0: F0Config::default(),
                input_gain: 1.0,
                noise_gate_enabled: false,
                noise_gate_threshold: 0.01,
                noise_gate_shaping: NoiseGateShaping::default(),
                output_extra_ms: 60,
                volume_excluded_ms: 40,
                extra_convert_ms: 80,
                output_gain: 1.0,
                output_dynamics: OutputDynamicsConfig::default(),
                progress: None,
            })?;
            let load_ms = load_started.elapsed().as_secs_f64() * 1000.0;
            let input: Vec<f32> = (0..timing.input_chunk_samples)
                .map(|index| {
                    ((index as f32 * 220.0 * std::f32::consts::TAU / sample_rate as f32).sin())
                        * 0.05
                })
                .collect();
            let mut audio = Vec::new();
            let mut pitch = Vec::new();
            let warmup_started = Instant::now();
            pipeline.process(&input, sample_rate, &mut audio, &mut pitch)?;
            let warmup_ms = warmup_started.elapsed().as_secs_f64() * 1000.0;
            let mut timings = Vec::with_capacity(frames);
            let mut output_rate = sample_rate;
            let mut voiced_ratio = 0.0;
            for _ in 0..frames {
                let inference_started = Instant::now();
                let result = pipeline.process(&input, sample_rate, &mut audio, &mut pitch)?;
                timings.push(inference_started.elapsed().as_secs_f64() * 1000.0);
                output_rate = result.sample_rate;
                voiced_ratio = result.voiced_ratio;
            }
            timings.sort_by(f64::total_cmp);
            let inference_ms = timings.iter().sum::<f64>() / timings.len() as f64;
            Ok(json!({
                "ok": true, "provider": provider_label, "loadMs": load_ms,
                "warmupMs": warmup_ms, "inferenceMs": inference_ms, "inputSamples": input.len(),
                "frames": frames, "p50Ms": percentile_sorted(&timings, 0.50),
                "p95Ms": percentile_sorted(&timings, 0.95), "p99Ms": percentile_sorted(&timings, 0.99),
                "maxMs": timings[timings.len() - 1], "outputSamples": audio.len(),
                "modelSampleRate": output_rate, "voicedRatio": voiced_ratio
            }))
        })
        .context("无法启动 RVC 自检线程")?;
    let report = worker
        .join()
        .map_err(|_| anyhow::anyhow!("RVC 自检线程异常终止"))??;
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
}

fn percentile_sorted(values: &[f64], percentile: f64) -> f64 {
    let index = ((values.len() - 1) as f64 * percentile).ceil() as usize;
    values[index.min(values.len() - 1)]
}

fn list_devices() -> Result<()> {
    let controller = EngineController::new(LiveParams::default());
    controller.refresh_devices(AudioHost::Wasapi, AudioHost::Wasapi)?;
    for _ in 0..50 {
        thread::sleep(Duration::from_millis(100));
        let (_, _, devices) = controller.snapshot();
        if let Some(error) = devices.error {
            bail!(error);
        }
        if !devices.inputs.is_empty() || !devices.outputs.is_empty() {
            println!(
                "{}",
                json!({"inputs": devices.inputs, "outputs": devices.outputs})
            );
            return Ok(());
        }
    }
    bail!("音频设备枚举超时")
}

fn run_engine(passthrough: bool) -> Result<()> {
    let arguments: Vec<String> = env::args().collect();
    let monitoring = passthrough && arguments.iter().any(|argument| argument == "--monitor");
    let output_gain_db = option_value(&arguments, "--output-gain-db")
        .map(str::parse)
        .transpose()
        .context("--output-gain-db 必须是数字")?
        .unwrap_or(0.0_f32);
    anyhow::ensure!(
        (-24.0..=24.0).contains(&output_gain_db),
        "--output-gain-db 必须在 -24 到 +24 dB 之间"
    );
    let noise_gate_enabled = arguments.iter().any(|argument| argument == "--noise-gate");
    let mut live = LiveParams {
        pitch_shift: option_value(&arguments, "--pitch")
            .map(str::parse)
            .transpose()
            .context("--pitch 必须是数字")?
            .unwrap_or(0.0),
        output_gain: db_to_gain(output_gain_db),
        noise_gate_enabled,
        ..LiveParams::default()
    };
    anyhow::ensure!(
        (-24.0..=24.0).contains(&live.pitch_shift),
        "--pitch 必须在 -24 到 +24 半音之间"
    );
    let controller = EngineController::new(live);
    let mut config = RealtimeConfig {
        passthrough,
        provider: selected_provider(&arguments)?,
        chunk_ms: if monitoring { 60 } else { 160 },
        crossfade_ms: if monitoring { 10 } else { 40 },
        sola_search_ms: if monitoring { 5 } else { 12 },
        extra_convert_ms: if monitoring { 20 } else { 80 },
        // Keep the lightweight gate stage available so the UI can toggle it
        // through live parameters without rebuilding the model pipeline.
        denoiser_mode: DenoiserMode::NoiseGate,
        ..RealtimeConfig::default()
    };
    if !passthrough {
        config.model = Some(required_path(&arguments, "--model")?);
        config.embedder = Some(required_path(&arguments, "--embedder")?);
        config.f0_model = Some(required_path(&arguments, "--f0")?);
    }
    config.input_device = option_value(&arguments, "--input").map(ToOwned::to_owned);
    config.output_device = option_value(&arguments, "--output").map(ToOwned::to_owned);
    let mut active_config = config.clone();
    controller.apply_config(config)?;
    let mut guard_profile = "normal";

    let (command_tx, command_rx) = mpsc::channel();
    thread::Builder::new()
        .name("foxvoice-stdin".into())
        .spawn(move || {
            for line in io::stdin().lock().lines().map_while(Result::ok) {
                if command_tx.send(line).is_err() {
                    break;
                }
            }
        })
        .context("无法启动实时参数控制线程")?;

    let mut last_report = Instant::now() - Duration::from_secs(1);
    loop {
        while let Ok(line) = command_rx.try_recv() {
            match serde_json::from_str::<EngineCommand>(&line) {
                Ok(EngineCommand::Live {
                    pitch,
                    output_gain_db,
                    noise_gate_enabled,
                }) => {
                    if (-24.0..=24.0).contains(&pitch) && (-24.0..=24.0).contains(&output_gain_db) {
                        live.pitch_shift = pitch;
                        live.output_gain = db_to_gain(output_gain_db);
                        live.noise_gate_enabled = noise_gate_enabled;
                        controller.set_live_params(live);
                    }
                }
                Ok(EngineCommand::Guard { level }) => {
                    let (chunk_ms, crossfade_ms, sola_search_ms, extra_convert_ms) =
                        match level.as_str() {
                            "normal" => (160, 40, 12, 80),
                            "stable" => (240, 50, 10, 60),
                            "survival" => (320, 60, 8, 40),
                            "bypass" => {
                                controller.set_passthrough(true);
                                guard_profile = "bypass";
                                continue;
                            }
                            _ => {
                                eprintln!("忽略未知游戏保护级别: {level}");
                                continue;
                            }
                        };
                    controller.set_passthrough(passthrough);
                    active_config.chunk_ms = if monitoring { 60 } else { chunk_ms };
                    active_config.crossfade_ms = if monitoring { 10 } else { crossfade_ms };
                    active_config.sola_search_ms = if monitoring { 5 } else { sola_search_ms };
                    active_config.extra_convert_ms = if monitoring { 20 } else { extra_convert_ms };
                    controller.apply_config(active_config.clone())?;
                    guard_profile = match level.as_str() {
                        "stable" => "stable",
                        "survival" => "survival",
                        _ => "normal",
                    };
                }
                Err(error) => eprintln!("忽略无效实时参数: {error}"),
            }
        }
        thread::sleep(Duration::from_millis(50));
        if last_report.elapsed() < Duration::from_secs(1) {
            continue;
        }
        last_report = Instant::now();
        let (status, telemetry, _) = controller.snapshot();
        println!(
            "{}",
            json!({
                "event": "engineStatus", "state": format!("{:?}", status.state),
                "passthrough": passthrough || guard_profile == "bypass",
                "guardProfile": guard_profile, "chunkMs": active_config.chunk_ms,
                "message": status.message, "detail": status.detail,
                "inputDevice": status.input_device, "outputDevice": status.output_device,
                "inputSampleRate": status.input_sample_rate, "outputSampleRate": status.output_sample_rate,
                "chunks": telemetry.chunks, "inferenceUs": telemetry.inference_us,
                "processingUs": telemetry.processing_us, "inputOverruns": telemetry.input_overruns,
                "outputUnderruns": telemetry.output_underruns,
                "outputDroppedSamples": telemetry.output_dropped_samples,
                "outputBufferSamples": telemetry.output_buffer_samples
            })
        );
        if status.state == EngineState::Error {
            bail!(status.detail.unwrap_or(status.message));
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
enum EngineCommand {
    Live {
        pitch: f32,
        output_gain_db: f32,
        noise_gate_enabled: bool,
    },
    Guard {
        level: String,
    },
}

fn db_to_gain(decibels: f32) -> f32 {
    10.0_f32.powf(decibels / 20.0)
}

fn required_path(arguments: &[String], name: &str) -> Result<PathBuf> {
    let path = PathBuf::from(
        option_value(arguments, name).with_context(|| format!("缺少参数 {name} <path>"))?,
    );
    anyhow::ensure!(path.is_file(), "文件不存在: {}", path.display());
    Ok(path)
}

fn option_value<'a>(arguments: &'a [String], name: &str) -> Option<&'a str> {
    arguments
        .iter()
        .position(|argument| argument == name)
        .and_then(|index| arguments.get(index + 1))
        .map(String::as_str)
}

fn selected_provider(arguments: &[String]) -> Result<Provider> {
    #[cfg(all(windows, feature = "windowsml"))]
    {
        match option_value(arguments, "--provider").unwrap_or("directml") {
            "directml" => Ok(Provider::WindowsMlDirectMl),
            "auto" => Ok(Provider::WindowsMl),
            "nvtrtx" => Ok(Provider::WindowsMlNvTensorRtRtx),
            value => bail!("未知 Windows ML 后端: {value}；可用值 directml, auto, nvtrtx"),
        }
    }
    #[cfg(not(all(windows, feature = "windowsml")))]
    {
        let _ = arguments;
        bail!("当前构建未启用 WindowsML；请使用 --features windowsml")
    }
}

fn provider_status() -> Result<()> {
    #[cfg(all(windows, feature = "windowsml"))]
    {
        let providers = vc_core::windows_ml::list_catalog_providers()?
            .into_iter()
            .map(|provider| {
                json!({
                    "name": provider.name,
                    "version": provider.version,
                    "readyState": provider.ready_state.label(),
                    "certification": provider.certification,
                    "libraryPath": provider.library_path
                })
            })
            .collect::<Vec<_>>();
        println!("{}", serde_json::to_string_pretty(&providers)?);
        Ok(())
    }
    #[cfg(not(all(windows, feature = "windowsml")))]
    {
        bail!("当前构建未启用 WindowsML；请使用 --features windowsml")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn linear_resampler_preserves_duration() {
        let input = vec![0.0_f32; 44_100];
        assert_eq!(resample_linear(&input, 44_100, 48_000).len(), 48_000);
        assert_eq!(resample_linear(&input, 44_100, 44_100).len(), 44_100);
    }

    #[test]
    fn percentile_uses_nearest_rank_without_exceeding_bounds() {
        let values = [1.0, 2.0, 3.0, 4.0, 5.0];
        assert_eq!(percentile_sorted(&values, 0.50), 3.0);
        assert_eq!(percentile_sorted(&values, 0.95), 5.0);
        assert_eq!(percentile_sorted(&values, 1.0), 5.0);
    }

    #[test]
    fn wav_reader_downmixes_stereo_pcm() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("stereo.wav");
        let spec = hound::WavSpec {
            channels: 2,
            sample_rate: 48_000,
            bits_per_sample: 16,
            sample_format: hound::SampleFormat::Int,
        };
        let mut writer = hound::WavWriter::create(&path, spec).unwrap();
        writer.write_sample(16_384_i16).unwrap();
        writer.write_sample(-16_384_i16).unwrap();
        writer.write_sample(8_192_i16).unwrap();
        writer.write_sample(8_192_i16).unwrap();
        writer.finalize().unwrap();
        let (mono, rate) = read_wav_mono(&path).unwrap();
        assert_eq!(rate, 48_000);
        assert_eq!(mono.len(), 2);
        assert!(mono[0].abs() < 0.0001);
        assert!((mono[1] - 0.25).abs() < 0.001);
    }

    #[test]
    fn soundboard_dsp_fades_edges_and_limits_peak() {
        let mut samples = vec![2.0; 1_000];
        apply_soundboard_dsp(&mut samples, 2.0, 48_000);
        assert!(samples[0] > 0.0 && samples[0] < 0.02);
        assert_eq!(samples[500], 1.0);
        assert!(samples[999] > 0.0 && samples[999] < 0.02);
        assert!(samples.iter().all(|sample| (-1.0..=1.0).contains(sample)));
    }
}
