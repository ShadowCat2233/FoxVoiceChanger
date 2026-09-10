use std::{env, path::PathBuf, thread, time::Duration};

use anyhow::{Context, Result, bail};
use serde_json::json;
use vc_app::{AudioHost, EngineController, EngineState, LiveParams, RealtimeConfig};
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
    let live = LiveParams {
        pitch_shift: option_value(&arguments, "--pitch")
            .map(str::parse)
            .transpose()
            .context("--pitch 必须是数字")?
            .unwrap_or(0.0),
        ..LiveParams::default()
    };
    let controller = EngineController::new(live);
    let mut config = RealtimeConfig {
        passthrough,
        provider: windows_provider()?,
        chunk_ms: 160,
        crossfade_ms: 40,
        sola_search_ms: 12,
        extra_convert_ms: 80,
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

    loop {
        thread::sleep(Duration::from_secs(1));
        let (status, telemetry, _) = controller.snapshot();
        println!(
            "{}",
            json!({
                "event": "engineStatus", "state": format!("{:?}", status.state),
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
