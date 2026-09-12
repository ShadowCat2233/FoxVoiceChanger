use std::{
    env, fs,
    net::{SocketAddr, TcpStream},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    time::Duration,
};

use anyhow::{Context, Result, bail};
use serde::Serialize;

pub const RVC_REPOSITORY: &str =
    "https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI.git";
pub const RVC_REVISION: &str = "81eed5e8f68b6bed1789f682fe78cdd324495afc";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TrainingStatus {
    pub root: PathBuf,
    pub source_ready: bool,
    pub python_ready: bool,
    pub torch_ready: bool,
    pub cuda_ready: bool,
    pub models_ready: bool,
    pub revision: String,
    pub ready: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TrainingOutput {
    pub path: PathBuf,
    pub kind: String,
    pub size_bytes: u64,
}

#[derive(Debug, Clone)]
pub struct TrainingRequest {
    pub dataset: PathBuf,
    pub name: String,
    pub epochs: u16,
    pub batch_size: u8,
    pub workers: u8,
}

pub fn training_root() -> Result<PathBuf> {
    let local = env::var_os("LOCALAPPDATA").context("LOCALAPPDATA 不可用")?;
    Ok(PathBuf::from(local).join("FoxVoice").join("training"))
}

pub fn status(root: &Path) -> Result<TrainingStatus> {
    let source = root.join("rvc");
    let python = root.join("venv").join("Scripts").join("python.exe");
    let source_ready =
        source.join("train").join("train.py").is_file() && source.join("LICENSE").is_file();
    let python_ready = python.is_file();
    let (torch_ready, cuda_ready) = if python_ready {
        let result = Command::new(&python)
            .args([
                "-c",
                "import json,torch;print(json.dumps({'torch':torch.__version__,'cuda':torch.cuda.is_available()}))",
            ])
            .output();
        match result {
            Ok(output) if output.status.success() => {
                let value: serde_json::Value =
                    serde_json::from_slice(&output.stdout).unwrap_or_default();
                (
                    value.get("torch").is_some(),
                    value.get("cuda").and_then(|v| v.as_bool()).unwrap_or(false),
                )
            }
            _ => (false, false),
        }
    } else {
        (false, false)
    };
    let models_ready = source
        .join("assets")
        .join("hubert_base")
        .join("pytorch_model.bin")
        .is_file()
        && source
            .join("assets")
            .join("rmvpe")
            .join("rmvpe.pt")
            .is_file()
        && source.join("assets").join("pretrained_v2").is_dir();
    Ok(TrainingStatus {
        root: root.to_path_buf(),
        source_ready,
        python_ready,
        torch_ready,
        cuda_ready,
        models_ready,
        revision: RVC_REVISION.into(),
        ready: source_ready && python_ready && torch_ready && models_ready,
    })
}

pub fn install(root: &Path, backend: &str, accepted: bool) -> Result<TrainingStatus> {
    anyhow::ensure!(
        accepted,
        "必须先确认 RVC 上游 MIT 许可证和训练权重的独立许可"
    );
    anyhow::ensure!(
        matches!(backend, "cuda" | "cpu"),
        "训练后端仅支持 cuda 或 cpu"
    );
    augment_windows_dependency_path();
    fs::create_dir_all(root).context("无法创建训练组件目录")?;
    ensure_command("git", "--version", "Git.Git")?;
    ensure_python312()?;
    ensure_command("ffmpeg", "-version", "Gyan.FFmpeg")?;

    let source = root.join("rvc");
    if !source.exists() {
        run(
            Command::new("git")
                .args(["clone", "--filter=blob:none", RVC_REPOSITORY])
                .arg(&source),
            "克隆 RVC 训练源代码",
        )?;
    }
    let origin = command_text(
        Command::new("git")
            .arg("-C")
            .arg(&source)
            .args(["remote", "get-url", "origin"]),
        "读取 RVC 上游地址",
    )?;
    anyhow::ensure!(
        origin
            .trim()
            .trim_end_matches('/')
            .eq_ignore_ascii_case(RVC_REPOSITORY.trim_end_matches('/')),
        "训练目录不是受信任的 RVC 上游 checkout"
    );
    run(
        Command::new("git").arg("-C").arg(&source).args([
            "fetch",
            "--depth",
            "1",
            "origin",
            RVC_REVISION,
        ]),
        "获取固定 RVC 版本",
    )?;
    run(
        Command::new("git")
            .arg("-C")
            .arg(&source)
            .args(["checkout", "--detach", RVC_REVISION]),
        "切换固定 RVC 版本",
    )?;

    let venv = root.join("venv");
    let python = venv.join("Scripts").join("python.exe");
    if !python.is_file() {
        run(
            Command::new("py").args(["-3.12", "-m", "venv"]).arg(&venv),
            "创建 Python 3.12 隔离环境",
        )?;
    }
    run(
        Command::new(&python).args([
            "-m",
            "pip",
            "install",
            "--upgrade",
            "pip",
            "setuptools<81",
            "wheel",
        ]),
        "更新训练环境打包工具",
    )?;
    let (requirements, torch_index) = if backend == "cuda" {
        (
            "requirments_cu128_py312.txt",
            "https://download.pytorch.org/whl/cu128",
        )
    } else {
        (
            "requirments_cpu_py312.txt",
            "https://download.pytorch.org/whl/cpu",
        )
    };
    let torch = if backend == "cuda" {
        "torch==2.7.1+cu128"
    } else {
        "torch==2.7.1+cpu"
    };
    let torchaudio = if backend == "cuda" {
        "torchaudio==2.7.1+cu128"
    } else {
        "torchaudio==2.7.1+cpu"
    };
    run(
        Command::new(&python).args([
            "-m",
            "pip",
            "install",
            torch,
            torchaudio,
            "--index-url",
            torch_index,
        ]),
        "安装固定 PyTorch 训练运行时",
    )?;
    let constraints = root.join("foxvoice-training-constraints.txt");
    fs::write(
        &constraints,
        "numpy==1.26.4\nopencv-python-headless==4.10.0.84\nscipy==1.13.1\nscikit-learn==1.6.1\n",
    )
    .context("无法写入训练依赖版本约束")?;
    run(
        Command::new(&python)
            .current_dir(&source)
            .args(["-m", "pip", "install", "-r", requirements, "-c"])
            .arg(&constraints),
        "安装 RVC 训练依赖",
    )?;
    run(
        Command::new(&python).args(["-m", "pip", "install", "huggingface_hub==0.36.2"]),
        "安装固定模型下载工具",
    )?;
    let hf = venv.join("Scripts").join("hf.exe");
    run(
        Command::new(&hf).current_dir(&source).args([
            "download",
            "lj1995/VoiceConversionWebUI",
            "--revision",
            "main",
            "--include",
            "hubert_base/*",
            "pretrained/*",
            "pretrained_v2/*",
            "--local-dir",
            "assets",
        ]),
        "下载 RVC 训练基础权重",
    )?;
    run(
        Command::new(&hf).current_dir(&source).args([
            "download",
            "lj1995/VoiceConversionWebUI",
            "rmvpe.pt",
            "--revision",
            "main",
            "--local-dir",
            "assets/rmvpe",
        ]),
        "下载 RMVPE 训练权重",
    )?;
    let report = status(root)?;
    anyhow::ensure!(report.ready, "训练组件安装结束，但完整自检未通过");
    if backend == "cuda" {
        anyhow::ensure!(
            report.cuda_ready,
            "CUDA PyTorch 已安装，但 torch.cuda.is_available() 为 false；请检查 NVIDIA 驱动"
        );
    }
    Ok(report)
}

pub fn launch_workbench(root: &Path) -> Result<()> {
    let report = status(root)?;
    anyhow::ensure!(report.ready, "训练环境尚未完整安装并通过自检");
    let source = root.join("rvc");
    let python = root.join("venv").join("Scripts").join("python.exe");
    eprintln!("FOXVOICE_TRAINING_STAGE=训练工作台正在启动：http://127.0.0.1:7865");
    let status = Command::new(python)
        .current_dir(&source)
        .args(["webui.py", "--noautoopen"])
        .status()
        .context("无法启动 RVC 训练工作台")?;
    anyhow::ensure!(
        status.success(),
        "RVC 训练工作台异常退出，退出码 {:?}",
        status.code()
    );
    Ok(())
}

pub fn run_training(root: &Path, request: &TrainingRequest) -> Result<()> {
    anyhow::ensure!(request.dataset.is_dir(), "训练数据集目录不存在");
    anyhow::ensure!(
        !request.name.is_empty()
            && request.name.len() <= 64
            && request
                .name
                .bytes()
                .all(|value| value.is_ascii_alphanumeric() || matches!(value, b'_' | b'-')),
        "实验名称只能包含 1-64 个英文字母、数字、下划线或连字符"
    );
    anyhow::ensure!(
        (1..=1200).contains(&request.epochs),
        "训练轮数必须为 1-1200"
    );
    anyhow::ensure!((1..=64).contains(&request.batch_size), "批大小必须为 1-64");
    anyhow::ensure!((1..=64).contains(&request.workers), "工作线程数必须为 1-64");
    let report = status(root)?;
    anyhow::ensure!(report.ready, "训练环境尚未完整安装并通过自检");

    let source = root.join("rvc");
    let python = root.join("venv").join("Scripts").join("python.exe");
    let bridge = root.join("foxvoice-training-bridge.py");
    fs::write(&bridge, include_bytes!("training_bridge.py"))
        .context("无法写入 FoxVoice 训练桥接脚本")?;
    let status = Command::new(python)
        .current_dir(&source)
        .arg(&bridge)
        .arg("--dataset")
        .arg(&request.dataset)
        .arg("--name")
        .arg(&request.name)
        .arg("--epochs")
        .arg(request.epochs.to_string())
        .arg("--batch")
        .arg(request.batch_size.to_string())
        .arg("--workers")
        .arg(request.workers.to_string())
        .status()
        .context("无法启动一键训练任务")?;
    anyhow::ensure!(status.success(), "一键训练失败，退出码 {:?}", status.code());
    Ok(())
}

pub fn outputs(root: &Path) -> Result<Vec<TrainingOutput>> {
    let source = root.join("rvc");
    let mut found = Vec::new();
    collect_outputs(
        &source.join("assets").join("weights"),
        "pytorchCheckpoint",
        "pth",
        &mut found,
    )?;
    collect_outputs(
        &source.join("assets").join("indices"),
        "faissIndex",
        "index",
        &mut found,
    )?;
    found.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(found)
}

fn collect_outputs(
    directory: &Path,
    kind: &str,
    extension: &str,
    found: &mut Vec<TrainingOutput>,
) -> Result<()> {
    if !directory.is_dir() {
        return Ok(());
    }
    for entry in fs::read_dir(directory)
        .with_context(|| format!("无法读取训练产物目录: {}", directory.display()))?
    {
        let entry = entry?;
        let path = entry.path();
        if entry.file_type()?.is_file()
            && path
                .extension()
                .and_then(|value| value.to_str())
                .is_some_and(|value| value.eq_ignore_ascii_case(extension))
        {
            found.push(TrainingOutput {
                size_bytes: entry.metadata()?.len(),
                path,
                kind: kind.into(),
            });
        }
    }
    Ok(())
}

fn ensure_command(command: &str, version_argument: &str, winget_id: &str) -> Result<()> {
    if Command::new(command)
        .arg(version_argument)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .is_ok_and(|value| value.success())
    {
        return Ok(());
    }
    run(
        Command::new(winget_executable()?).args([
            "install",
            "--id",
            winget_id,
            "--exact",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
        ]),
        &format!("自动安装 {winget_id}"),
    )?;
    anyhow::ensure!(
        Command::new(command)
            .arg(version_argument)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .is_ok_and(|value| value.success()),
        "{winget_id} 安装完成但当前进程尚未发现命令，请重启 FoxVoice 后重试"
    );
    Ok(())
}

fn ensure_python312() -> Result<()> {
    let available = || {
        Command::new("py")
            .args(["-3.12", "--version"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .is_ok_and(|value| value.success())
    };
    if available() {
        return Ok(());
    }
    run(
        Command::new(winget_executable()?).args([
            "install",
            "--id",
            "Python.Python.3.12",
            "--exact",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
        ]),
        "自动安装 Python.Python.3.12",
    )?;
    anyhow::ensure!(
        available(),
        "Python 3.12 安装完成但 Python Launcher 尚未发现它，请重启 FoxVoice 后重试"
    );
    Ok(())
}

fn winget_executable() -> Result<PathBuf> {
    if Command::new("winget")
        .arg("--version")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .is_ok_and(|status| status.success())
    {
        return Ok(PathBuf::from("winget"));
    }
    if let Some(local) = env::var_os("LOCALAPPDATA") {
        let alias = PathBuf::from(local)
            .join("Microsoft")
            .join("WindowsApps")
            .join("winget.exe");
        if alias.is_file() {
            return Ok(alias);
        }
    }
    bail!("未找到 Windows Package Manager (winget)。请先从 Microsoft Store 安装‘应用安装程序’，然后重试")
}

fn augment_windows_dependency_path() {
    let mut paths = env::split_paths(&env::var_os("PATH").unwrap_or_default()).collect::<Vec<_>>();
    if let Some(local) = env::var_os("LOCALAPPDATA") {
        let local = PathBuf::from(local);
        paths.push(local.join("Microsoft").join("WinGet").join("Links"));
        paths.push(local.join("Programs").join("Python").join("Launcher"));
        paths.push(local.join("Microsoft").join("WindowsApps"));
    }
    if let Some(program_files) = env::var_os("ProgramFiles") {
        paths.push(PathBuf::from(program_files).join("Git").join("cmd"));
    }
    paths.retain(|path| path.is_dir());
    paths.dedup();
    if let Ok(joined) = env::join_paths(paths) {
        // The supervisor is a single-threaded short-lived CLI. Updating its own PATH here
        // lets dependencies installed by winget become visible without restarting FoxVoice.
        unsafe { env::set_var("PATH", joined) };
    }
}

fn run(command: &mut Command, label: &str) -> Result<()> {
    eprintln!("FOXVOICE_TRAINING_STAGE={label}");
    apply_network_proxy(command);
    let status = command
        .status()
        .with_context(|| format!("无法启动：{label}"))?;
    if !status.success() {
        bail!("{label}失败，退出码 {:?}", status.code());
    }
    Ok(())
}

fn apply_network_proxy(command: &mut Command) {
    if env::var_os("HTTPS_PROXY").is_some() || env::var_os("https_proxy").is_some() {
        return;
    }
    for port in [7897_u16, 7890_u16] {
        let address = SocketAddr::from(([127, 0, 0, 1], port));
        if TcpStream::connect_timeout(&address, Duration::from_millis(120)).is_ok() {
            let proxy = format!("http://127.0.0.1:{port}");
            command.env("HTTPS_PROXY", &proxy).env("HTTP_PROXY", &proxy);
            eprintln!("FOXVOICE_TRAINING_STAGE=已检测到本机网络代理，继续下载训练组件");
            return;
        }
    }
}

fn command_text(command: &mut Command, label: &str) -> Result<String> {
    let output = command
        .output()
        .with_context(|| format!("无法启动：{label}"))?;
    anyhow::ensure!(output.status.success(), "{label}失败");
    String::from_utf8(output.stdout).context("命令输出不是 UTF-8")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_training_environment_reports_not_ready() {
        let temporary = tempfile::tempdir().unwrap();
        let report = status(temporary.path()).unwrap();
        assert!(!report.ready);
        assert!(!report.source_ready);
        assert!(!report.python_ready);
        assert_eq!(report.revision, RVC_REVISION);
    }

    #[test]
    fn installer_requires_license_acceptance_before_writing() {
        let temporary = tempfile::tempdir().unwrap();
        let root = temporary.path().join("training");
        assert!(install(&root, "cuda", false).is_err());
        assert!(!root.exists());
    }

    #[test]
    fn installer_rejects_unknown_backend_before_writing() {
        let temporary = tempfile::tempdir().unwrap();
        let root = temporary.path().join("training");
        assert!(install(&root, "rocm", true).is_err());
        assert!(!root.exists());
    }

    #[test]
    fn lists_only_supported_training_outputs() {
        let temporary = tempfile::tempdir().unwrap();
        let weights = temporary.path().join("rvc/assets/weights");
        let indices = temporary.path().join("rvc/assets/indices");
        fs::create_dir_all(&weights).unwrap();
        fs::create_dir_all(&indices).unwrap();
        fs::write(weights.join("voice.pth"), b"weight").unwrap();
        fs::write(weights.join("notes.txt"), b"ignore").unwrap();
        fs::write(indices.join("voice.index"), b"index").unwrap();
        let found = outputs(temporary.path()).unwrap();
        assert_eq!(found.len(), 2);
        assert_eq!(found[0].kind, "faissIndex");
        assert_eq!(found[1].kind, "pytorchCheckpoint");
    }

    #[test]
    fn invalid_training_request_is_rejected_before_launch() {
        let temporary = tempfile::tempdir().unwrap();
        let request = TrainingRequest {
            dataset: temporary.path().to_path_buf(),
            name: "bad name".into(),
            epochs: 20,
            batch_size: 4,
            workers: 4,
        };
        assert!(run_training(temporary.path(), &request).is_err());
    }
}
