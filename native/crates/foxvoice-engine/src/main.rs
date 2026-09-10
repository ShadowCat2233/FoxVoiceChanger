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
use vc_core::Provider;

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
        _ => bail!("可用命令: devices, passthrough, rvc"),
    }
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
        chunk_ms: 160,
        crossfade_ms: 40,
        sola_search_ms: 12,
        extra_convert_ms: 80,
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
    controller.apply_config(config)?;

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
                "passthrough": passthrough,
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
