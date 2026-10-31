namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [MfaPhoneMap: 内部音素と固定日本語MFAモデルの一音素対応を保持する]
//   処理: [1: workerのMfaPronunciationと同じ対応で記録の元ラベルを照合する]
// }
internal static class MfaPhoneMap
{
    // { 責務: [Encode: 対応するMFAモデル音素を返し、未対応の音素はnullを返す] }
    internal static string? Encode(string phone) => phone switch
    {
        "a" or "i" or "e" or "o" or "k" or "s" or "z" or "ts" or "t" or "d" or "n" or "h" or "b" or "p" or "m" or "w" or "v" => phone,
        "u" => "ɯ",
        "g" => "ɡ",
        "r" => "ɾ",
        "y" => "j",
        "N" => "ɴ",
        "sh" => "ɕ",
        "ch" => "tɕ",
        "j" => "dʑ",
        "f" => "ɸ",
        "ky" => "c",
        "gy" => "ɟ",
        "I" => "i̥",
        "U" => "ɯ̥",
        "ny" => "ɲ",
        "hy" => "ç",
        "my" => "mʲ",
        "by" => "bʲ",
        "py" => "pʲ",
        "ry" => "ɾʲ",
        "ty" => "tʲ",
        "dy" => "dʲ",
        _ => null
    };
}
