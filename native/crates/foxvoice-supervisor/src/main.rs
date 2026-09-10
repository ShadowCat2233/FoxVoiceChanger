use std::{env, fs, io::Cursor, path::PathBuf, process::Command};

#[cfg(feature = "wasapi")]
use std::{thread, time::Duration};

use anyhow::{Context, Result, bail};
use foxvoice_audio::audio_ring_buffer;
use foxvoice_contracts::{
    ControlCommand, ControlRequest, DoctorCheck, DoctorReport, EngineAvailability,
    IPC_PROTOCOL_VERSION, PerformanceSample,
};
use foxvoice_ipc::{read_frame, write_frame};
use foxvoice_models::ModelLibrary;
use foxvoice_supervisor::{
    ComponentManifest, GameGuard, detect_hardware, foundation_model_status, foundation_root,
    install_foundation_models, recommend_engine,
};

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
        "models" => run_models_command(),
        "foundation-models" => run_foundation_models_command(),
        #[cfg(feature = "wasapi")]
        "audio-devices" => print_audio_devices(),
        #[cfg(feature = "wasapi")]
        "bypass-test" => run_bypass_test(),
        #[cfg(feature = "wasapi")]
        "bypass-run" => run_bypass_forever(),
        _ => bail!(
            "未知命令。可用命令: doctor, recommend, validate-components, guard-demo, ipc-demo, audio-buffer-demo, models, foundation-models{}",
            if cfg!(feature = "wasapi") {
                ", audio-devices, bypass-test, bypass-run"
            } else {
                "（audio-devices 需使用 --features wasapi 构建）"
            }
        ),
    }
}

fn run_foundation_models_command() -> Result<()> {
    let action = env::args().nth(2).unwrap_or_else(|| "status".into());
    let root = foundation_root()?;
    let states = match action.as_str() {
        "status" => foundation_model_status(&root)?,
        "install" => install_foundation_models(
            &root,
            env::args().any(|argument| argument == "--accept-gpl"),
        )?,
        _ => bail!("用法: foxvoice-supervisor foundation-models status|install --accept-gpl"),
    };
    println!("{}", serde_json::to_string_pretty(&states)?);
    Ok(())
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

fn run_models_command() -> Result<()> {
    let action = env::args().nth(2).unwrap_or_else(|| "list".into());
    let library = ModelLibrary::open(model_library_path()?)?;
    match action.as_str() {
        "list" => println!("{}", serde_json::to_string_pretty(&library.list()?)?),
        "import" => {
            let path = env::args().nth(3).map(PathBuf::from).context(
                "用法: foxvoice-supervisor models import <model.onnx|model.pth|model.index>",
            )?;
            println!(
                "{}",
                serde_json::to_string_pretty(&library.import_file(&path, None)?)?
            );
        }
        "huggingface" => {
            let url = env::args()
                .nth(3)
                .context("用法: foxvoice-supervisor models huggingface <resolve-url>")?;
            println!(
                "{}",
                serde_json::to_string_pretty(&library.import_huggingface(&url)?)?
            );
        }
        "recycle" => {
            let id = env::args()
                .nth(3)
                .context("用法: foxvoice-supervisor models recycle <model-id>")?;
            println!(
                "{}",
                serde_json::json!({"ok": true, "recycledTo": library.recycle(&id)?})
            );
        }
        "resolve" => {
            let id = env::args()
                .nth(3)
                .context("用法: foxvoice-supervisor models resolve <model-id>")?;
            println!("{}", serde_json::json!({"path": library.model_file(&id)?}));
        }
        "convert" => {
            let id = env::args()
                .nth(3)
                .context("用法: foxvoice-supervisor models convert <model-id>")?;
            let source = library.model_file(&id)?;
            anyhow::ensure!(
                source
                    .extension()
                    .and_then(|value| value.to_str())
                    .is_some_and(|value| value.eq_ignore_ascii_case("pth")),
                "只有 .pth 检查点需要转换"
            );
            let converter = converter_executable()?;
            let output = Command::new(&converter)
                .arg(&source)
                .output()
                .with_context(|| format!("无法启动模型转换器: {}", converter.display()))?;
            anyhow::ensure!(
                output.status.success(),
                "模型转换失败: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            );
            let response: serde_json::Value =
                serde_json::from_slice(&output.stdout).context("模型转换器返回了无效结果")?;
            let onnx = response
                .get("path")
                .and_then(|value| value.as_str())
                .map(PathBuf::from)
                .context("模型转换器没有返回输出路径")?;
            let result = library.import_file(&onnx, Some(format!("converted-from:{id}")));
            if onnx.is_file() {
                let _ = fs::remove_file(&onnx);
            }
            println!("FOXVOICE_RESULT_JSON={}", serde_json::to_string(&result?)?);
        }
        _ => bail!(
            "未知模型命令。可用命令: models list, models import, models huggingface, models recycle, models resolve, models convert"
        ),
    }
    Ok(())
}

fn converter_executable() -> Result<PathBuf> {
    if let Some(path) = env::var_os("FOXVOICE_CONVERTER") {
        let path = PathBuf::from(path);
        anyhow::ensure!(path.is_file(), "FOXVOICE_CONVERTER 指向的文件不存在");
        return Ok(path);
    }
    let current = env::current_exe().context("无法定位控制服务")?;
    let name = if cfg!(windows) {
        "foxvoice-converter.exe"
    } else {
        "foxvoice-converter"
    };
    let path = current.parent().context("控制服务缺少父目录")?.join(name);
    anyhow::ensure!(path.is_file(), "模型转换组件未安装: {}", path.display());
    Ok(path)
}

fn model_library_path() -> Result<PathBuf> {
    if let Some(path) = env::var_os("FOXVOICE_DATA_DIR") {
        return Ok(PathBuf::from(path).join("models"));
    }
    let local_app_data = env::var_os("LOCALAPPDATA").context("系统缺少 LOCALAPPDATA")?;
    Ok(PathBuf::from(local_app_data)
        .join("FoxVoice")
        .join("models"))
}

#[cfg(feature = "wasapi")]
fn print_audio_devices() -> Result<()> {
    println!(
        "{}",
        serde_json::to_string_pretty(&foxvoice_audio::enumerate_devices()?)?
    );
    Ok(())
}

#[cfg(feature = "wasapi")]
fn run_bypass_test() -> Result<()> {
    let seconds = env::args()
        .nth(2)
        .map(|value| value.parse::<u64>())
        .transpose()
        .context("旁路测试时长必须是整数秒")?
        .unwrap_or(2);
    anyhow::ensure!(
        (1..=10).contains(&seconds),
        "旁路测试时长必须在 1–10 秒之间"
    );

    let bypass = foxvoice_audio::start_safe_bypass(None, None, 80)?;
    thread::sleep(Duration::from_secs(seconds));
    let metrics = bypass.metrics();
    println!(
        "{}",
        serde_json::json!({
            "ok": true,
            "durationSeconds": seconds,
            "sampleRate": bypass.sample_rate,
            "inputChannels": bypass.input_channels,
            "outputChannels": bypass.output_channels,
            "bufferMs": bypass.buffer_ms,
            "metrics": {
                "inputOverruns": metrics.input_overruns,
                "outputUnderruns": metrics.output_underruns,
                "streamErrors": metrics.stream_errors
            }
        })
    );
    Ok(())
}

#[cfg(feature = "wasapi")]
fn run_bypass_forever() -> Result<()> {
    let bypass = foxvoice_audio::start_safe_bypass(None, None, 80)?;
    println!(
        "{}",
        serde_json::json!({
            "event": "audioStarted",
            "mode": "safeBypass",
            "sampleRate": bypass.sample_rate,
            "inputChannels": bypass.input_channels,
            "outputChannels": bypass.output_channels,
            "bufferMs": bypass.buffer_ms
        })
    );
    loop {
        thread::park_timeout(Duration::from_secs(1));
        let metrics = bypass.metrics();
        println!(
            "{}",
            serde_json::json!({
                "event": "audioMetrics",
                "inputOverruns": metrics.input_overruns,
                "outputUnderruns": metrics.output_underruns,
                "streamErrors": metrics.stream_errors
            })
        );
    }
}
