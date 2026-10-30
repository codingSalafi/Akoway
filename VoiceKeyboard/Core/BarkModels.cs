using VoiceKeyboard.Models;

namespace VoiceKeyboard.Core;

/// <summary>
/// One file of a downloadable model. Large files are hosted Brotli-compressed
/// (<c>Name</c> + ".br") and unpacked after download.
/// </summary>
public sealed record BarkModelFile(string Name, long Size, string Sha256,
                                   long? BrotliSize = null, string? BrotliSha256 = null)
{
    public bool IsCompressed => BrotliSize != null;
    public string DownloadName => IsCompressed ? Name + ".br" : Name;
    public long DownloadSize => BrotliSize ?? Size;
    public string DownloadSha256 => BrotliSha256 ?? Sha256;
}

/// <summary>
/// Everything the app needs to fetch and run one speech model
/// (files from tools/bark-onnx/export_bark_onnx.py).
/// </summary>
public sealed record BarkModelSpec(
    SpeechModel Model,
    string FolderName,
    string RepoUrl,
    double LeadInSeconds,              // silence the model was evaluated with before the audio
    IReadOnlyList<BarkModelFile> Files)
{
    public long DownloadBytes => Files.Sum(f => f.DownloadSize);
}

public static class BarkModels
{
    public static readonly BarkModelSpec Pro = new(
        SpeechModel.BarkPro,
        "bark-3.0-pro",
        "https://huggingface.co/codingSalafi/akoway-bark-3.0-pro-onnx/resolve/main/",
        LeadInSeconds: 0.25,           // Underdog's reference script prepends 0.25 s
        new BarkModelFile[]
        {
            new("preprocessor.onnx",      1_192_352, "b7e95a3d22e474366f1d79b1c9a20178d57c896d8f9d5c58b2f6bbf5f29ddda0"),
            new("encoder.int8.onnx",    839_417_684, "377211191029a797971cc2eaf2a5aa653c1577319e76579dddd16241a890183f"),
            new("decoder.onnx",          48_874_113, "1d74e62cc3068b769ff8a2db4ea422c438fd02d19a1d0ebe69925332d14ae93d"),
            new("joiner.onnx",           21_020_669, "acbeb1c099f997611bf4b52a91595722ca1da76f83a3d43fa0ddba5167eb048e"),
            new("tokens.txt",                93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
            new("LICENSE-NOTICE.txt",           896, "15d87485db928258e079bbe5959e8ccbc3022c1e871ce596417d7b52d97964d0"),
        });

    public static readonly BarkModelSpec Clean = new(
        SpeechModel.BarkClean,
        "bark-3.0-clean",
        "https://huggingface.co/codingSalafi/akoway-bark-3.0-clean-onnx/resolve/main/",
        LeadInSeconds: 0.0,            // Clean was evaluated with no leading silence
        new BarkModelFile[]
        {
            new("preprocessor.onnx", 1_192_352, "b7e95a3d22e474366f1d79b1c9a20178d57c896d8f9d5c58b2f6bbf5f29ddda0",
                217_903, "34edece7371af6c9eafa7222b4cfdd29cc0811e64c1c22817033d467dee01cb1"),
            new("encoder.int8.onnx", 633_625_522, "92625103654e9fe470029b9ee99ba14b13258d8d67158b9c0c4404581ebbee65",
                223_935_501, "b03e489e2c25f3ffc5331cae03db81b622bb3106cf1bc6a53a2cea7f94a9bffc"),
            new("decoder.onnx", 48_874_113, "0fec09f96f6a7a53e6d9c2dd0fd419d8916bcd814b9ae56113b09f5bcccc1043",
                11_408_295, "9bd893964c40c5f6caec6bcd28e00f2790371b7b16f8578cfcc7bc28a8a95f99"),
            new("joiner.onnx", 21_020_669, "4e82e839deae49c2cb60e275174e3d64d3bad5addc92a906bfb0d1ae1efb3765",
                7_617_892, "39d3cacd5a2eaac6cb0ed24b6ed0d11ee80e7282088774db698fa1e22f5b291a"),
            new("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d",
                35_787, "28b1bb53eff2182f80ded65d231ea8bd960e75b92fc154a489eb3865ad8a1d9e"),
            new("LICENSE-NOTICE.txt", 897, "240be8df9f067be93af0e654cc078e447fbd1861490df6226cd06d55677863b8"),
        });

    public static BarkModelSpec For(SpeechModel model) => model switch
    {
        SpeechModel.BarkClean => Clean,
        _ => Pro
    };
}
