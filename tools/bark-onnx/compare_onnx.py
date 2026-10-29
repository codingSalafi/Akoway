#!/usr/bin/env python3
"""
Check the exported ONNX model against Underdog's reference transformers
pipeline (runtime/transcribe_bark_pro.py: 0.25 s of leading silence, greedy TDT).

The ONNX path below is the same algorithm as Core/BarkEngine.cs, so this script
is also the reference for the C# port.

Usage:
    python compare_onnx.py --model ./bark-pro --onnx ./bark-pro-onnx [--refs refs.json] a.wav b.wav ...
"""
import argparse
import json
import time
from pathlib import Path

import jiwer
import numpy as np
import onnxruntime as ort
import soundfile as sf
import torch
from transformers import AutoProcessor, ParakeetForTDT

SAMPLE_RATE = 16000
LEAD_IN = np.zeros(SAMPLE_RATE // 4, np.float32)
MAX_SYMBOLS_PER_FRAME = 10


class BarkOnnx:
    def __init__(self, folder: Path, threads: int = 4):
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads
        load = lambda name: ort.InferenceSession(str(folder / name), opts, providers=["CPUExecutionProvider"])
        self.pre, self.enc = load("preprocessor.onnx"), load("encoder.int8.onnx")
        self.dec, self.joi = load("decoder.onnx"), load("joiner.onnx")
        self.tokens = [line.rsplit(" ", 1)[0] for line in open(folder / "tokens.txt", encoding="utf-8")]
        self.blank = len(self.tokens) - 1
        h = self.dec.get_inputs()[1].shape
        self.layers, self.hidden = h[0], h[2]

    def transcribe(self, audio: np.ndarray) -> str:
        wave = np.concatenate([LEAD_IN, audio]).astype(np.float32)[None, :]
        feats, frames = self.pre.run(None, {"waveforms": wave, "lengths": np.array([wave.shape[1]], np.int64)})
        enc, enc_len = self.enc.run(None, {"features": feats, "feature_lengths": frames})

        h = np.zeros((self.layers, 1, self.hidden), np.float32)
        c = np.zeros_like(h)
        dec, h, c = self.dec.run(None, {"targets": np.array([[self.blank]], np.int64), "states_h": h, "states_c": c})

        ids, t, emitted = [], 0, 0
        while t < enc_len[0]:
            logits = self.joi.run(None, {"encoder_outputs": enc[:, :, t:t + 1], "decoder_outputs": dec})[0].reshape(-1)
            token = int(np.argmax(logits[:self.blank + 1]))
            skip = int(np.argmax(logits[self.blank + 1:]))
            if token != self.blank:
                ids.append(token)
                dec, h, c = self.dec.run(None, {"targets": np.array([[token]], np.int64), "states_h": h, "states_c": c})
                emitted += 1
            if skip == 0 and (token == self.blank or emitted >= MAX_SYMBOLS_PER_FRAME):
                skip = 1
            if skip > 0:
                emitted = 0
            t += skip

        pieces = (self.tokens[i] for i in ids if not self.tokens[i].startswith("<"))
        return "".join(pieces).replace("▁", " ").strip()


def norm(text: str) -> str:
    return " ".join("".join(c for c in text.lower() if c.isalnum() or c in " '").split())


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True)
    p.add_argument("--onnx", required=True)
    p.add_argument("--refs", help="JSON {wav name: ground-truth text}")
    p.add_argument("wavs", nargs="+")
    args = p.parse_args()

    refs = json.load(open(args.refs)) if args.refs else {}
    processor = AutoProcessor.from_pretrained(args.model)
    model = ParakeetForTDT.from_pretrained(args.model, dtype=torch.float32).eval()
    bark = BarkOnnx(Path(args.onnx))

    diffs, truth, hf_all, onnx_all, rtfs = [], [], [], [], []
    for wav in args.wavs:
        audio, sr = sf.read(wav, dtype="float32", always_2d=True)
        audio = audio.mean(axis=1)
        assert sr == SAMPLE_RATE, f"{wav}: expected 16 kHz"

        inputs = processor(np.concatenate([LEAD_IN, audio]), sampling_rate=SAMPLE_RATE, return_tensors="pt")
        with torch.inference_mode():
            ids = model.generate(**inputs)
        ids = ids.sequences if hasattr(ids, "sequences") else ids
        hf_text = processor.batch_decode(ids, skip_special_tokens=True)[0].strip()

        t0 = time.perf_counter()
        onnx_text = bark.transcribe(audio)
        rtfs.append((time.perf_counter() - t0) / (len(audio) / SAMPLE_RATE))

        name = Path(wav).name
        if hf_text != onnx_text:
            print(f"\n== {name}\n  hf  : {hf_text}\n  onnx: {onnx_text}")
        if name in refs:
            truth.append(norm(refs[name]))
            hf_all.append(norm(hf_text))
            onnx_all.append(norm(onnx_text))
        diffs.append(jiwer.wer(norm(hf_text), norm(onnx_text)) if norm(hf_text) else 0.0)

    print(f"\nClips: {len(args.wavs)}, identical text: {sum(d == 0 for d in diffs)}")
    print(f"Word difference ONNX vs reference: {100 * np.mean(diffs):.2f}%   ONNX mean RTF: {np.mean(rtfs):.3f}")
    if truth:
        print(f"WER vs ground truth: reference {100 * jiwer.wer(truth, hf_all):.2f}%  onnx {100 * jiwer.wer(truth, onnx_all):.2f}%")


if __name__ == "__main__":
    main()
