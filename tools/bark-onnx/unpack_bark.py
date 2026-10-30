#!/usr/bin/env python3
"""
Unpack an Underdog .bark file (e.g. Bark 3.0 Clean) into a transformers folder that
export_bark_onnx.py can read. Unpacking is exact; it uses Underdog's own bark_loader.py,
and the processor/tokenizer files come from nvidia/parakeet-tdt-0.6b-v3, as that loader does.

Usage:
    hf download ConwayResearch/Underdog-Bark-3.0-Clean --local-dir bark-clean
    python unpack_bark.py bark-clean bark-clean-hf
"""
import shutil
import sys
from pathlib import Path

from huggingface_hub import hf_hub_download


def main():
    src, out = Path(sys.argv[1]), Path(sys.argv[2])
    sys.path.insert(0, str(src))
    from bark_loader import load_model  # Underdog's loader, shipped next to model.bark

    model = load_model(str(src / "model.bark"))
    model.save_pretrained(out)
    for name in ["processor_config.json", "tokenizer.json", "tokenizer_config.json"]:
        shutil.copy(hf_hub_download("nvidia/parakeet-tdt-0.6b-v3", name), out / name)
    shutil.copy(src / "NOTICE", out / "NOTICE")
    print(f"Unpacked to {out}")


if __name__ == "__main__":
    main()
