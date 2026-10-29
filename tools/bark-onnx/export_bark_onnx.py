#!/usr/bin/env python3
"""
Export Underdog Bark 3.0 Pro (a Parakeet TDT 0.6B fine-tune, Hugging Face
transformers format) to ONNX for Akoway, which runs it with ONNX Runtime from C#
(see Core/BarkEngine.cs).

Output (in --out):
    preprocessor.onnx   (1, N) waveform + (1,) length -> (1, 128, T) log-mel + (1,) frames
                        Bit-for-bit the same maths as ParakeetFeatureExtractor.
    encoder.int8.onnx   (1, 128, T) + (1,) -> (1, 640, T') + (1,)
                        The 1024 -> 640 encoder projector is folded in. Only MatMul
                        weights are int8; convolutions stay fp32 (full int8 costs WER).
    decoder.onnx        LSTM prediction network, one token per call
    joiner.onnx         -> (1, 1, 1, 8193 token logits + 5 duration logits)
    tokens.txt          "<piece> <id>" per line, blank last
    LICENSE-NOTICE.txt  CC-BY-4.0 attribution

Usage:
    python export_bark_onnx.py --model ./bark-pro --out ./bark-pro-onnx
"""
import argparse
import math
import os
import shutil
from pathlib import Path

import torch
import torch.nn.functional as F
from onnxruntime.quantization import QuantType, quantize_dynamic
from tokenizers import Tokenizer
from torch import nn
from transformers import ParakeetFeatureExtractor, ParakeetForTDT

LOG_ZERO_GUARD = 2 ** -24
EPSILON = 1e-5


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


def export(module, inputs, path, input_names, output_names, dynamic_axes, **kw):
    torch.onnx.export(module, inputs, str(path), input_names=input_names, output_names=output_names,
                      dynamic_axes=dynamic_axes, opset_version=17, dynamo=False, **kw)


@torch.no_grad()
def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True, help="Local Bark 3.0 Pro (transformers) directory")
    parser.add_argument("--out", required=True, help="Output directory")
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
    quantize_dynamic(str(tmp / "encoder.onnx"), str(out / "encoder.int8.onnx"),
                     weight_type=QuantType.QUInt8, op_types_to_quantize=["MatMul"])
    shutil.rmtree(tmp)

    export(DecoderWrapper(model),
           (torch.tensor([[cfg.blank_token_id]]), torch.zeros(layers, 1, hidden), torch.zeros(layers, 1, hidden)),
           out / "decoder.onnx", ["targets", "states_h", "states_c"], ["decoded", "next_states_h", "next_states_c"], {})

    export(JoinerWrapper(model), (torch.randn(1, hidden, 1), torch.randn(1, hidden, 1)), out / "joiner.onnx",
           ["encoder_outputs", "decoder_outputs"], ["logits"], {})

    (out / "LICENSE-NOTICE.txt").write_text(
        "Underdog Bark 3.0 Pro by Conway Research, https://huggingface.co/ConwayResearch/Underdog-Bark-3.0-Pro\n"
        "Derived from nvidia/parakeet-tdt-0.6b-v3 (NVIDIA) and moondream/parakeet-ultra (Moondream).\n"
        "Licensed CC-BY-4.0. Changes: converted to ONNX, encoder MatMul weights quantized to int8.\n\n"
        + (src / "NOTICE").read_text(encoding="utf-8"),
        encoding="utf-8")

    os.system(f"ls -l '{out}'")


if __name__ == "__main__":
    main()
