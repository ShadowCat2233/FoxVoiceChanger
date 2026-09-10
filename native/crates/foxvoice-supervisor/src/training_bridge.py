"""FoxVoice bridge for the pinned RVC training implementation.

This file deliberately loads only the function-definition portion of the pinned
webui.py. Importing that module normally launches Gradio as a side effect.
"""

import argparse
import hashlib
import os
import sys
from pathlib import Path


EXPECTED_WEBUI_SHA256 = "da00da29f50b2e733a82f3509ff47888db68ea47219ea5cc2df3efdebf9baa32"
UI_MARKER = '\nwith gr.Blocks(title="RVC WebUI", css=TRAINING_INFO_CSS) as app:'


def stage(message):
    print(f"FOXVOICE_TRAINING_STAGE={message}", file=sys.stderr, flush=True)


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--epochs", required=True, type=int)
    parser.add_argument("--batch", required=True, type=int)
    parser.add_argument("--workers", required=True, type=int)
    return parser.parse_args()


def load_training_namespace(root):
    webui = root / "webui.py"
    source = webui.read_bytes()
    digest = hashlib.sha256(source.replace(b"\r\n", b"\n")).hexdigest()
    if digest != EXPECTED_WEBUI_SHA256:
        raise RuntimeError(
            "固定 RVC webui.py 校验失败；请在组件中心重新安装训练环境"
        )
    text = source.decode("utf-8")
    if text.count(UI_MARKER) != 1:
        raise RuntimeError("固定 RVC 训练入口结构不匹配")
    prefix = text.split(UI_MARKER, 1)[0]
    namespace = {"__name__": "foxvoice_rvc_training", "__file__": str(webui)}
    original_argv = sys.argv
    try:
        sys.argv = [str(webui), "--noautoopen"]
        exec(compile(prefix, str(webui), "exec"), namespace)
    finally:
        sys.argv = original_argv
    return namespace


def main():
    args = parse_args()
    root = Path.cwd()
    dataset = Path(args.dataset).resolve()
    if not dataset.is_dir():
        raise RuntimeError("训练数据集目录不存在")

    ns = load_training_namespace(root)
    state = {
        "stop_requested": False,
        "processes": [],
        "name": "FoxVoice 一键训练",
    }
    name = args.name
    no = ns["i18n"]("否")
    yes = ns["i18n"]("是")
    has_cuda = str(ns["config"].device).startswith("cuda")
    gpu = "0" if has_cuda else ""
    pretrained_g = "assets/pretrained_v2/f0G40k.pth"
    pretrained_d = "assets/pretrained_v2/f0D40k.pth"
    save_every = max(1, min(args.epochs, 10))

    stage("1/4 正在切分并清洗数据集")
    for _ in ns["run_preprocess_dataset"](
        str(dataset), name, "40k", args.workers, state, False, None
    ):
        pass
    stage("2/4 正在提取 RMVPE 音高与 HuBERT 特征")
    for _ in ns["run_extract_f0_feature"](
        gpu, args.workers, "rmvpe", True, name, "v2", gpu, state, False
    ):
        pass
    stage("3/4 正在训练 RVC v2 F0 模型")
    for _ in ns["run_train_model"](
        name, "40k", True, 0, save_every, args.epochs, args.batch,
        no, pretrained_g, pretrained_d, gpu, no, yes, "v2", state, False, None
    ):
        pass
    stage("4/4 正在生成检索索引")
    for _ in ns["run_train_index"](name, "v2", state, False, None):
        pass
    stage("训练完成；正在扫描输出模型")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        stage(f"训练失败：{error}")
        raise
