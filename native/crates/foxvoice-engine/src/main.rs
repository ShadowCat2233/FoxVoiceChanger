use std::{
    env,
    io::{self, BufRead},
    path::PathBuf,
    sync::mpsc,
    thread,
    time::{Duration, Instant},
};

use anyhow::{Context, Result, bail};
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
        _ => bail!("可用命令: devices, passthrough, rvc, validate-rvc"),
    }
}

fn validate_rvc() -> Result<()> {
    let arguments: Vec<String> = env::args().collect();
    let model = required_path(&arguments, "--model")?;
    let embedder = required_path(&arguments, "--embedder")?;
    let f0_model = required_path(&arguments, "--f0")?;
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
                provider: windows_provider()?,
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
            let inference_started = Instant::now();
            let result = pipeline.process(&input, sample_rate, &mut audio, &mut pitch)?;
            let inference_ms = inference_started.elapsed().as_secs_f64() * 1000.0;
            Ok(json!({
                "ok": true, "provider": "windowsml-directml", "loadMs": load_ms,
                "inferenceMs": inference_ms, "inputSamples": input.len(),
                "outputSamples": audio.len(), "modelSampleRate": result.sample_rate,
                "voicedRatio": result.voiced_ratio
            }))
        })
        .context("无法启动 RVC 自检线程")?;
    let report = worker
        .join()
        .map_err(|_| anyhow::anyhow!("RVC 自检线程异常终止"))??;
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
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
        provider: windows_provider()?,
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

fn windows_provider() -> Result<Provider> {
    #[cfg(all(windows, feature = "windowsml"))]
    {
        Ok(Provider::WindowsMlDirectMl)
    }
    #[cfg(not(all(windows, feature = "windowsml")))]
    {
        bail!("当前构建未启用 WindowsML；请使用 --features windowsml")
    }
}
