use std::{env, io::Cursor, path::PathBuf};

use anyhow::{Context, Result, bail};
use foxvoice_audio::audio_ring_buffer;
use foxvoice_contracts::{
    ControlCommand, ControlRequest, DoctorCheck, DoctorReport, EngineAvailability,
    IPC_PROTOCOL_VERSION, PerformanceSample,
};
use foxvoice_ipc::{read_frame, write_frame};
use foxvoice_supervisor::{ComponentManifest, GameGuard, detect_hardware, recommend_engine};

fn main() {
    if let Err(error) = run() {
        eprintln!("foxvoice-supervisor: {error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let command = env::args().nth(1).unwrap_or_else(|| "doctor".into());
    match command.as_str() {
        "doctor" => print_doctor(),
        "recommend" => print_recommendation(),
        "validate-components" => {
            let path = env::args()
                .nth(2)
                .map(PathBuf::from)
                .unwrap_or_else(|| PathBuf::from("native/config/components.json"));
            let manifest = ComponentManifest::load(&path)
                .with_context(|| format!("组件清单校验失败: {}", path.display()))?;
            println!(
                "{}",
                serde_json::json!({
                    "ok": true,
                    "schemaVersion": manifest.schema_version,
                    "componentCount": manifest.components.len()
                })
            );
            Ok(())
        }
        "guard-demo" => print_guard_demo(),
        "ipc-demo" => print_ipc_demo(),
        "audio-buffer-demo" => print_audio_buffer_demo(),
        #[cfg(feature = "wasapi")]
        "audio-devices" => print_audio_devices(),
        _ => bail!(
            "未知命令。可用命令: doctor, recommend, validate-components, guard-demo, ipc-demo, audio-buffer-demo{}",
            if cfg!(feature = "wasapi") {
                ", audio-devices"
            } else {
                "（audio-devices 需使用 --features wasapi 构建）"
            }
        ),
    }
}

fn availability() -> EngineAvailability {
    EngineAvailability {
        windows_ml: cfg!(target_os = "windows"),
        tensor_rt: env::var_os("FOXVOICE_TENSORRT_READY").is_some(),
        cuda: env::var_os("FOXVOICE_CUDA_READY").is_some(),
    }
}

fn print_doctor() -> Result<()> {
    let profile = detect_hardware()?;
    let recommendation = recommend_engine(&profile, &availability());
    let report = DoctorReport {
        checks: vec![
            DoctorCheck {
                id: "windows-x64".into(),
                ok: cfg!(all(target_os = "windows", target_arch = "x86_64")),
                message: "FoxVoice 首个原生版本要求 Windows x64。".into(),
            },
            DoctorCheck {
                id: "graphics-adapter".into(),
                ok: !profile.adapters.is_empty(),
                message: if profile.adapters.is_empty() {
                    "未能枚举图形适配器，将只允许 CPU 安全模式。".into()
                } else {
                    format!("检测到 {} 个图形适配器。", profile.adapters.len())
                },
            },
            DoctorCheck {
                id: "index-policy".into(),
                ok: true,
                message: "游戏实时模式默认 indexRate=0。".into(),
            },
        ],
        profile,
        recommendation,
    };
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
}

fn print_recommendation() -> Result<()> {
    let profile = detect_hardware()?;
    println!(
        "{}",
        serde_json::to_string_pretty(&recommend_engine(&profile, &availability()))?
    );
    Ok(())
}

fn print_guard_demo() -> Result<()> {
    let mut guard = GameGuard::default();
    for sequence in 0..6 {
        let decision = guard.observe(PerformanceSample {
            inference_ms: 21.5,
            chunk_budget_ms: 20.0,
            gpu_load_percent: 99.0,
            underruns: sequence,
        });
        println!("{}", serde_json::to_string(&decision)?);
    }
    Ok(())
}

fn print_ipc_demo() -> Result<()> {
    let request = ControlRequest {
        protocol_version: IPC_PROTOCOL_VERSION,
        request_id: 1,
        command: ControlCommand::Doctor,
    };
    let mut bytes = Vec::new();
    write_frame(&mut bytes, &request)?;
    let decoded: ControlRequest = read_frame(&mut Cursor::new(&bytes))?;
    println!("{}", serde_json::to_string_pretty(&decoded)?);
    Ok(())
}

fn print_audio_buffer_demo() -> Result<()> {
    let (mut producer, mut consumer, metrics) = audio_ring_buffer(8);
    producer.push_interleaved_as_mono(&[0.25, 0.75, -0.5, 0.5], 2);
    let mut output = [0.0_f32; 4];
    consumer.fill_interleaved(&mut output, 2);
    println!(
        "{}",
        serde_json::json!({
            "output": output,
            "overruns": metrics.overruns(),
            "underruns": metrics.underruns()
        })
    );
    Ok(())
}

#[cfg(feature = "wasapi")]
fn print_audio_devices() -> Result<()> {
    println!(
        "{}",
        serde_json::to_string_pretty(&foxvoice_audio::enumerate_devices()?)?
    );
    Ok(())
}
