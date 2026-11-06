using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// 登録された質感プリセット1件。Status は「実現できるか」の表示用で、選択や記録を妨げない。
public sealed record TexturePresetEntry(TexturePresetRecord Preset, string Source, string Status, string Reason, string Hash);

public sealed record TexturePresetRegistration(bool Accepted, TexturePresetEntry? Entry, string? Failure);

// 組み込みとModの質感プリセットを同じ規則で検証・登録する。(id, version) が一意キー。
// 読み込みに失敗したModは、そのModだけを拒否して他は使い続ける。
public sealed class TexturePresetRegistry
{
    private const string NoRealizationReason =
        "この版では、この質感を実現する学習工程がまだ登録されていません。選択と記録はできますが、効果は保証されません。";

    private readonly List<TexturePresetEntry> _entries = [];
    private readonly List<(string Origin, string Reason)> _failures = [];
    private readonly Func<TexturePresetRecord, (string Status, string Reason)> _realization;

    // realization: 学習戦略の登録(#137)などが、実現できるかを返す。未指定なら実現工程なし(unsupported)。
    public TexturePresetRegistry(Func<TexturePresetRecord, (string Status, string Reason)>? realization = null)
    {
        _realization = realization ?? (_ => (TexturePresetStatus.Unsupported, NoRealizationReason));
        foreach (var preset in BuiltInTexturePresets.All) Add(preset, TexturePresetSource.BuiltIn);
    }

    public IReadOnlyList<(string Origin, string Reason)> Failures => _failures;

    // 列挙順: id → SemVer → 提供元(組み込みが先)。
    public IReadOnlyList<TexturePresetEntry> Entries => _entries
        .OrderBy(x => x.Preset.PresetId, StringComparer.Ordinal)
        .ThenBy(x => x.Preset.Version, Comparer<string>.Create(TexturePresetValidator.CompareVersions))
        .ThenBy(x => x.Source == TexturePresetSource.BuiltIn ? 0 : 1).ToArray();

    // {
    //   責務: [RegisterMod: Modのpreset.jsonを検証して登録する]
    //   処理: [
    //     1: 形式を検証する(失敗はそのModだけを invalid として記録する)
    //     2: 組み込みIDの乗っ取りと、同じ (id, version) の別内容を拒否する
    //     3: 同じ (id, version) で同じ内容なら何もしない
    //   ]
    //   戻り値: [registration: 受理の可否と、拒否理由]
    // }
    public TexturePresetRegistration RegisterMod(string json, string origin)
    {
        TexturePresetRecord preset;
        try
        {
            preset = TexturePresetValidator.Parse(json);
        }
        catch (InvalidDataException exception)
        {
            return Reject(origin, exception.Message);
        }
        if (_entries.Any(x => x.Source == TexturePresetSource.BuiltIn && x.Preset.PresetId == preset.PresetId) ||
            preset.PresetId.StartsWith("builtin.", StringComparison.Ordinal))
            return Reject(origin, $"組み込みのIDは使えません: {preset.PresetId}");
        var hash = TexturePresetValidator.Hash(preset);
        var same = _entries.FirstOrDefault(x => x.Preset.PresetId == preset.PresetId && x.Preset.Version == preset.Version);
        if (same is not null)
            return same.Hash == hash
                ? new TexturePresetRegistration(true, same, null)
                : Reject(origin, $"同じID・versionで内容が異なります: {preset.PresetId} {preset.Version}");
        return new TexturePresetRegistration(true, Add(preset, TexturePresetSource.Mod), null);
    }

    public TexturePresetRegistration RegisterModFile(string path)
    {
        try
        {
            return RegisterMod(File.ReadAllText(path), path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Reject(path, $"ファイルを読めません: {exception.Message}");
        }
    }

    public TexturePresetEntry? Find(string presetId, string? version = null)
    {
        var candidates = _entries.Where(x => x.Preset.PresetId == presetId && (version is null || x.Preset.Version == version));
        return candidates.OrderByDescending(x => x.Preset.Version, Comparer<string>.Create(TexturePresetValidator.CompareVersions))
            .FirstOrDefault();
    }

    // {
    //   責務: [Select: 出力targetとは独立にプリセットを選び、ジョブへ保存する質感要求にする]
    //   処理: [1: ID・versionを解決する 2: 出力targetに対応していなければ拒否する 3: 参考区間と共に要求を作る]
    //   エラー: [未登録・target非対応のとき ArgumentException。unsupported でも選択はでき、状態は Entry に残る]
    // }
    public (TrainingTextureRequest Request, TexturePresetEntry Entry) Select(
        string presetId, string? version, string target, IEnumerable<string>? referenceSegmentIds = null)
    {
        var entry = Find(presetId, version) ?? throw new ArgumentException($"登録されていない質感プリセットです: {presetId}");
        if (!entry.Preset.SupportedTargets.Contains(target))
            throw new ArgumentException($"{entry.Preset.DisplayNameJa} は {target} に対応していません。");
        return (new TrainingTextureRequest
        {
            Preset = entry.Preset.PresetId, PresetVersion = entry.Preset.Version,
            ReferenceSegmentIds = [.. referenceSegmentIds ?? []]
        }, entry);
    }

    private TexturePresetEntry Add(TexturePresetRecord preset, string source)
    {
        var (status, reason) = _realization(preset);
        var entry = new TexturePresetEntry(preset, source, status, reason, TexturePresetValidator.Hash(preset));
        _entries.Add(entry);
        return entry;
    }

    private TexturePresetRegistration Reject(string origin, string reason)
    {
        _failures.Add((origin, reason));
        return new TexturePresetRegistration(false, null, reason);
    }
}
