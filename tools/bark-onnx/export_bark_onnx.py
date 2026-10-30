#!/usr/bin/env python3
"""
Export an Underdog Bark 3.0 model (a Parakeet TDT 0.6B fine-tune, Hugging Face
transformers format) to ONNX for Akoway, which runs it with ONNX Runtime from C#
(see Core/BarkEngine.cs).

Output (in --out):
    preprocessor.onnx   (1, N) waveform + (1,) length -> (1, 128, T) log-mel + (1,) frames
                        Bit-for-bit the same maths as ParakeetFeatureExtractor.
    encoder.int8.onnx   (1, 128, T) + (1,) -> (1, 640, T') + (1,)
                        The 1024 -> 640 encoder projector is folded in. Only MatMul
                        weights are int8; convolutions stay fp32 (full int8 costs WER).
                        With --per-row-int8 every large encoder matrix gets its own
                        8-bit scale per row instead (MatMuls and pointwise convs), run
                        as MatMulNBits with full-precision activations. Use it for
                        Bark 3.0 Clean/Plus, whose weights hold only a few values per
                        row: one int8 grid per tensor would round them, and Clean also
                        drifts when activations are quantized. Matches Underdog's
                        reference transcripts exactly on our test set.
    decoder.onnx        LSTM prediction network, one token per call
    joiner.onnx         -> (1, 1, 1, 8193 token logits + 5 duration logits)
    tokens.txt          "<piece> <id>" per line, blank last
    LICENSE-NOTICE.txt  CC-BY-4.0 attribution

Usage:
    python export_bark_onnx.py --model ./bark-pro --out ./bark-pro-onnx
    python export_bark_onnx.py --model ./bark-clean-hf --out ./bark-clean-onnx --per-row-int8 \
        --repo ConwayResearch/Underdog-Bark-3.0-Clean
"""
import argparse
import math
import os
import shutil
from pathlib import Path

import numpy as np
import onnx
import torch
import torch.nn.functional as F
from onnx import helper, numpy_helper
from onnxruntime.quantization import QuantType, quantize_dynamic
from tokenizers import Tokenizer
from torch import nn
from transformers import ParakeetFeatureExtractor, ParakeetForTDT

LOG_ZERO_GUARD = 2 ** -24
EPSILON = 1e-5
MIN_PER_ROW_PARAMS = 100_000  # smaller matrices stay fp32


class Preprocessor(nn.Module):
    """ParakeetFeatureExtractor as a graph: preemphasis, STFT (as a DFT conv),
    power, slaney mel, log, per-feature normalization over valid frames."""

    def __init__(self, fe: ParakeetFeatureExtractor):
        super().__init__()
        self.preemphasis = fe.preemphasis
        self.hop = fe.hop_length
        self.n_fft = fe.n_fft
        window = torch.hann_window(fe.win_length, periodic=False, dtype=torch.float64)
        pad = (fe.n_fft - fe.win_length) // 2  # torch.stft centres a short window
        window = F.pad(window, (pad, fe.n_fft - fe.win_length - pad))
        n = torch.arange(fe.n_fft, dtype=torch.float64)
        k = torch.arange(fe.n_fft // 2 + 1, dtype=torch.float64)
        angle = 2 * math.pi * k[:, None] * n[None, :] / fe.n_fft
        self.register_buffer("dft_real", (torch.cos(angle) * window)[:, None, :].float())
        self.register_buffer("dft_imag", (-torch.sin(angle) * window)[:, None, :].float())
        self.register_buffer("mel", fe.mel_filters.clone())

    def forward(self, waveforms: torch.Tensor, lengths: torch.Tensor):
        x = torch.cat([waveforms[:, :1], waveforms[:, 1:] - self.preemphasis * waveforms[:, :-1]], dim=1)
        x = F.pad(x[:, None, :], (self.n_fft // 2, self.n_fft // 2))  # center=True, constant pad
        real = F.conv1d(x, self.dft_real, stride=self.hop)
        imag = F.conv1d(x, self.dft_imag, stride=self.hop)
        power = real * real + imag * imag
        mel = torch.log(torch.matmul(self.mel, power) + LOG_ZERO_GUARD)  # (1, 128, T)

        frames = torch.div(lengths, self.hop, rounding_mode="floor")
        mask = (torch.arange(mel.shape[2], device=mel.device)[None, :] < frames[:, None]).to(mel.dtype)[:, None, :]
        count = frames.to(mel.dtype)[:, None, None]
        mean = (mel * mask).sum(dim=2, keepdim=True) / count
        var = (((mel * mask) - mean) ** 2 * mask).sum(dim=2, keepdim=True) / (count - 1)
        features = (mel - mean) / (torch.sqrt(var) + EPSILON) * mask
        return features, frames


class EncoderWrapper(nn.Module):
    def __init__(self, model: ParakeetForTDT):
        super().__init__()
        self.encoder = model.encoder
        self.projector = model.encoder_projector

    def forward(self, features: torch.Tensor, length: torch.Tensor):
        x = features.transpose(1, 2)  # transformers wants (B, T, C)
        mask = torch.arange(x.shape[1], device=x.device)[None, :] < length[:, None]
        out = self.encoder(input_features=x, attention_mask=mask.long())
        encoded = self.projector(out.last_hidden_state)
        return encoded.transpose(1, 2), out.attention_mask.sum(-1).to(torch.int64)


class DecoderWrapper(nn.Module):
    def __init__(self, model: ParakeetForTDT):
        super().__init__()
        self.embedding = model.decoder.embedding
        self.lstm = model.decoder.lstm
        self.projector = model.decoder.decoder_projector

    def forward(self, targets, states_h, states_c):
        out, (h, c) = self.lstm(self.embedding(targets), (states_h, states_c))
        return self.projector(out).transpose(1, 2), h, c


class JoinerWrapper(nn.Module):
    def __init__(self, model: ParakeetForTDT):
        super().__init__()
        self.activation = model.joint.activation
        self.head = model.joint.head

    def forward(self, encoder_outputs, decoder_outputs):
        enc = encoder_outputs.transpose(1, 2).unsqueeze(2)  # (B, T, 1, H)
        dec = decoder_outputs.transpose(1, 2).unsqueeze(1)  # (B, 1, U, H)
        return self.head(self.activation(enc + dec))


def row_int8(W: np.ndarray):
    """Per row of W [rows, cols]: the int8 scale that represents the row best, and the codes.

    Bark weights often hold only a few distinct values per row, so for each row we try
    every grid max/q (q = 1..127) and keep the one that lands those values closest to
    integers; for ordinary rows this simply ends up at q = 127."""
    W = W.astype(np.float64)
    m = np.abs(W).max(axis=1)
    m[m == 0] = 1.0
    # Search on each row's few largest distinct magnitudes, then verify on the whole row
    mags = np.sort(np.abs(W), axis=1)[:, ::-1]
    distinct = np.concatenate([np.ones((len(W), 1), bool), np.diff(mags, axis=1) < -1e-12 * m[:, None]], axis=1)
    rank = np.cumsum(distinct, axis=1)
    probe = np.stack([np.where(distinct & (rank == k), mags, 0).max(axis=1) for k in range(1, 9)], axis=1)
    best_err = np.full(len(W), np.inf)
    best_q = np.full(len(W), 127.0)
    for q in range(1, 128):
        s = (m / q)[:, None]
        err = np.abs(probe - np.round(probe / s) * s).max(axis=1)
        better = err < best_err - 1e-15
        best_err[better], best_q[better] = err[better], q
    scale = m / best_q
    codes = np.clip(np.round(W / scale[:, None]), -127, 127).astype(np.int8)
    # Rows with more than 8 distinct magnitudes were only probed: fall back to q = 127
    full_err = np.abs(codes * scale[:, None] - W).max(axis=1)
    std_scale = m / 127
    std_codes = np.clip(np.round(W / std_scale[:, None]), -127, 127).astype(np.int8)
    worse = full_err > np.abs(std_codes * std_scale[:, None] - W).max(axis=1)
    codes[worse], scale[worse] = std_codes[worse], std_scale[worse]
    return scale.astype(np.float32), codes


def quantize_per_row(src: Path, dst: Path):
    """Rewrite an fp32 encoder: MatMul(x, W) -> MatMulNBits (8-bit W with one scale per output
    column, fp32 activations), and large Conv weights -> int8 + per-channel DequantizeLinear
    (folded at load time)."""
    model = onnx.load(str(src))
    graph = model.graph
    inits = {i.name: i for i in graph.initializer}
    new_nodes, drop, add = [], set(), []
    for node in graph.node:
        w_name = node.input[1] if len(node.input) > 1 else None
        w = numpy_helper.to_array(inits[w_name]) if w_name in inits else None
        if node.op_type == "MatMul" and w is not None and w.ndim == 2 and w.size >= MIN_PER_ROW_PARAMS:
            scale, codes = row_int8(w.T)                      # MatMul weight is [in, out]: rows are columns
            base = w_name.replace(":", "_")
            k_in, n_out = w.shape
            block = next(b for b in (128, 64, 32, 16) if k_in % b == 0)
            blocks = k_in // block
            # MatMulNBits wants [N, blocks, block] uint8 with a scale/zero point per block; every
            # block of a row reuses that row's scale, so the weights stay exact. Activations
            # stay fp32 (accuracy_level 0): quantizing them is what made Clean drift.
            shifted = (codes.astype(np.int16) + 128).astype(np.uint8).reshape(n_out, blocks, block)
            add += [numpy_helper.from_array(shifted, base + "_q"),
                    numpy_helper.from_array(np.repeat(scale, blocks), base + "_scale"),
                    numpy_helper.from_array(np.full(n_out * blocks, 128, np.uint8), base + "_zp")]
            new_nodes.append(helper.make_node(
                "MatMulNBits", [node.input[0], base + "_q", base + "_scale", base + "_zp"],
                list(node.output), name=node.name + "_q", domain="com.microsoft",
                K=k_in, N=n_out, bits=8, block_size=block, accuracy_level=0))
            drop.add(w_name)
        elif node.op_type == "Conv" and w is not None and w.size >= MIN_PER_ROW_PARAMS:
            scale, codes = row_int8(w.reshape(w.shape[0], -1))
            base = w_name.replace(":", "_")
            add += [numpy_helper.from_array(codes.reshape(w.shape), base + "_q"),
                    numpy_helper.from_array(scale, base + "_scale"),
                    numpy_helper.from_array(np.zeros_like(codes[:, 0]), base + "_zp")]
            new_nodes.append(helper.make_node(
                "DequantizeLinear", [base + "_q", base + "_scale", base + "_zp"], [base + "_dq"],
                name=node.name + "_dq", axis=0))
            node.input[1] = base + "_dq"
            new_nodes.append(node)
            drop.add(w_name)
        else:
            new_nodes.append(node)
    del graph.node[:]
    graph.node.extend(new_nodes)
    kept = [i for i in graph.initializer if i.name not in drop]
    del graph.initializer[:]
    graph.initializer.extend(kept + add)
    if not any(o.domain == "com.microsoft" for o in model.opset_import):
        model.opset_import.append(helper.make_opsetid("com.microsoft", 1))
    onnx.save(model, str(dst))


def export(module, inputs, path, input_names, output_names, dynamic_axes, **kw):
    torch.onnx.export(module, inputs, str(path), input_names=input_names, output_names=output_names,
                      dynamic_axes=dynamic_axes, opset_version=17, dynamo=False, **kw)


@torch.no_grad()
def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True, help="Local Bark 3.0 (transformers) directory")
    parser.add_argument("--out", required=True, help="Output directory")
    parser.add_argument("--repo", default="ConwayResearch/Underdog-Bark-3.0-Pro",
                        help="Hugging Face repo the weights came from, for the licence notice")
    parser.add_argument("--per-row-int8", action="store_true",
                        help="per-row int8 for every large encoder matrix (for Bark 3.0 Clean/Plus)")
    args = parser.parse_args()

    src, out = Path(args.model), Path(args.out)
    tmp = out / "fp32-encoder"
    tmp.mkdir(parents=True, exist_ok=True)

    model = ParakeetForTDT.from_pretrained(src, dtype=torch.float32, attn_implementation="eager").eval()
    fe = ParakeetFeatureExtractor.from_pretrained(src)
    cfg = model.config
    hidden, layers = cfg.decoder_hidden_size, cfg.num_decoder_layers
    assert cfg.blank_token_id == cfg.vocab_size - 1, "BarkEngine assumes blank is the last token"
    assert list(cfg.durations) == [0, 1, 2, 3, 4], "BarkEngine uses the duration index as the frame skip"

    tokenizer = Tokenizer.from_file(str(src / "tokenizer.json"))
    with open(out / "tokens.txt", "w", encoding="utf-8") as f:
        for i in range(cfg.vocab_size - 1):
            f.write(f"{tokenizer.id_to_token(i)} {i}\n")
        f.write(f"<blk> {cfg.vocab_size - 1}\n")

    export(Preprocessor(fe), (torch.randn(1, 32000), torch.tensor([32000])), out / "preprocessor.onnx",
           ["waveforms", "lengths"], ["features", "feature_lengths"],
           {"waveforms": {1: "N"}, "features": {2: "T"}})

    export(EncoderWrapper(model), (torch.randn(1, cfg.encoder_config.num_mel_bins, 200), torch.tensor([200])),
           tmp / "encoder.onnx", ["features", "feature_lengths"], ["encoded", "encoded_lengths"],
           {"features": {2: "T"}, "encoded": {2: "T_out"}}, external_data=True)
    if args.per_row_int8:
        quantize_per_row(tmp / "encoder.onnx", out / "encoder.int8.onnx")
    else:
        quantize_dynamic(str(tmp / "encoder.onnx"), str(out / "encoder.int8.onnx"),
                         weight_type=QuantType.QUInt8, op_types_to_quantize=["MatMul"])
    shutil.rmtree(tmp)

    export(DecoderWrapper(model),
           (torch.tensor([[cfg.blank_token_id]]), torch.zeros(layers, 1, hidden), torch.zeros(layers, 1, hidden)),
           out / "decoder.onnx", ["targets", "states_h", "states_c"], ["decoded", "next_states_h", "next_states_c"], {})

    export(JoinerWrapper(model), (torch.randn(1, hidden, 1), torch.randn(1, hidden, 1)), out / "joiner.onnx",
           ["encoder_outputs", "decoder_outputs"], ["logits"], {})

    (out / "LICENSE-NOTICE.txt").write_text(
        f"{args.repo.split('/')[-1].replace('-', ' ')} by Conway Research, https://huggingface.co/{args.repo}\n"
        "Derived from nvidia/parakeet-tdt-0.6b-v3 (NVIDIA) and moondream/parakeet-ultra (Moondream).\n"
        "Licensed CC-BY-4.0. Changes: converted to ONNX, encoder weights quantized to int8.\n\n"
        + (src / "NOTICE").read_text(encoding="utf-8"),
        encoding="utf-8")

    os.system(f"ls -l '{out}'")


if __name__ == "__main__":
    main()
