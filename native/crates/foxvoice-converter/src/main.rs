use std::{env, path::PathBuf, thread};

use anyhow::{Context, Result};

fn main() {
    if let Err(error) = run() {
        eprintln!("foxvoice-converter: {error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let arguments: Vec<String> = env::args().collect();
    let source = arguments
        .get(1)
        .cloned()
        .map(PathBuf::from)
        .context("用法: foxvoice-converter <rvc-v2-f0.pth> [--webui]")?;
    anyhow::ensure!(source.is_file(), "检查点不存在: {}", source.display());
    anyhow::ensure!(
        source
            .extension()
            .and_then(|value| value.to_str())
            .is_some_and(|value| value.eq_ignore_ascii_case("pth")),
        "只接受 .pth 检查点"
    );
    let export_mode = if arguments.iter().any(|argument| argument == "--webui") {
        vc_convert::ExportMode::Webui
    } else {
        vc_convert::ExportMode::Streaming
    };
    let worker = thread::Builder::new()
        .name("foxvoice-pth-converter".into())
        .stack_size(64 * 1024 * 1024)
        .spawn(move || {
            vc_convert::convert_pth_file(
                &source,
                &vc_convert::ConvertOptions {
                    export_mode,
                    ..vc_convert::ConvertOptions::default()
                },
                &mut |stage| eprintln!("{}", stage.label()),
            )
        })
        .context("无法启动隔离转换线程")?;
    let output = worker
        .join()
        .map_err(|_| anyhow::anyhow!("转换线程异常终止"))??;
    println!("{}", serde_json::json!({ "path": output }));
    Ok(())
}
