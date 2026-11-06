using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

public sealed record TrainingStrategyEntry(
    TrainingStrategyDescriptor Strategy, string Source, string Status, string Reason, string Hash);

public sealed record TrainingStrategyRegistration(bool Accepted, TrainingStrategyEntry? Entry, string? Failure);

// 組み込みとModの学習戦略を同じ規則で検証・登録する。(id, version) が一意キー。
// 宣言は実能力を意味しない: 未登録の operation を含む戦略は拒否し、必要な能力が無い戦略は unsupported にする。
public sealed class TrainingStrategyRegistry(OperationRegistry operations, IReadOnlySet<string>? availableCapabilities = null)
{
    private readonly List<TrainingStrategyEntry> _entries = [];
    private readonly List<(string Origin, string Reason)> _failures = [];
    private readonly IReadOnlySet<string> _capabilities = availableCapabilities ?? new HashSet<string>();

    public IReadOnlyList<(string Origin, string Reason)> Failures => _failures;

    // 列挙順: id → SemVer → 提供元(組み込みが先)。表示名やロケールには依存しない。
    public IReadOnlyList<TrainingStrategyEntry> Entries => _entries
        .OrderBy(x => x.Strategy.StrategyId, StringComparer.Ordinal)
        .ThenBy(x => x.Strategy.Version, Comparer<string>.Create(TexturePresetValidator.CompareVersions))
        .ThenBy(x => x.Source == TexturePresetSource.BuiltIn ? 0 : 1).ToArray();

    // 組み込み戦略の登録(#133/#134 が提供する)。検証に失敗したら例外にして、壊れた組み込みを黙って捨てない。
    public TrainingStrategyEntry RegisterBuiltIn(TrainingStrategyDescriptor strategy)
    {
        if (!strategy.StrategyId.StartsWith("builtin.", StringComparison.Ordinal))
            throw new ArgumentException($"組み込み戦略のIDは builtin. で始まる必要があります: {strategy.StrategyId}");
        TrainingStrategyValidator.ValidateDag(strategy, operations);
        return Add(strategy, TexturePresetSource.BuiltIn);
    }

    // {
    //   責務: [RegisterMod: Modのstrategy.jsonを検証して登録する]
    //   処理: [
    //     1: 形式とStage DAGを検証する(失敗はそのModだけを拒否して記録する)
    //     2: 組み込みIDの乗っ取りと、同じ (id, version) の別内容を拒否する
    //     3: 同じ (id, version) で同じ内容なら何もしない
    //   ]
    // }
    public TrainingStrategyRegistration RegisterMod(string json, string origin)
    {
        TrainingStrategyDescriptor strategy;
        try
        {
            strategy = TrainingStrategyValidator.Parse(json);
            TrainingStrategyValidator.ValidateDag(strategy, operations);
        }
        catch (InvalidDataException exception)
        {
            return Reject(origin, exception.Message);
        }
        if (strategy.StrategyId.StartsWith("builtin.", StringComparison.Ordinal) ||
            _entries.Any(x => x.Source == TexturePresetSource.BuiltIn && x.Strategy.StrategyId == strategy.StrategyId))
            return Reject(origin, $"組み込みのIDは使えません: {strategy.StrategyId}");
        var hash = TrainingStrategyValidator.Hash(strategy);
        var same = _entries.FirstOrDefault(x => x.Strategy.StrategyId == strategy.StrategyId && x.Strategy.Version == strategy.Version);
        if (same is not null)
            return same.Hash == hash
                ? new TrainingStrategyRegistration(true, same, null)
                : Reject(origin, $"同じID・versionで内容が異なります: {strategy.StrategyId} {strategy.Version}");
        return new TrainingStrategyRegistration(true, Add(strategy, TexturePresetSource.Mod), null);
    }

    // 登録の削除。保存済みのジョブは TrainingStrategyReference に内容の hash を持つため影響を受けない。
    public bool Remove(string strategyId, string version) =>
        _entries.RemoveAll(x => x.Strategy.StrategyId == strategyId && x.Strategy.Version == version) > 0;

    public TrainingStrategyEntry? Find(string strategyId, string? version = null) => _entries
        .Where(x => x.Strategy.StrategyId == strategyId && (version is null || x.Strategy.Version == version))
        .OrderByDescending(x => x.Strategy.Version, Comparer<string>.Create(TexturePresetValidator.CompareVersions)).FirstOrDefault();

    // 計画に保存する参照(戦略・プリセット・棚卸しの版)。
    public static TrainingStrategyReference ReferenceOf(
        TrainingStrategyEntry entry, TexturePresetEntry? preset = null, SpeakerDataInventory? inventory = null) => new()
    {
        StrategyId = entry.Strategy.StrategyId, Version = entry.Strategy.Version, Hash = entry.Hash,
        PresetId = preset?.Preset.PresetId, PresetVersion = preset?.Preset.Version, InventoryRevision = inventory?.InventoryRevision
    };

    private TrainingStrategyEntry Add(TrainingStrategyDescriptor strategy, string source)
    {
        var missing = strategy.RequiredCapabilities.Where(x => !_capabilities.Contains(x)).ToArray();
        var entry = new TrainingStrategyEntry(strategy, source,
            missing.Length == 0 ? TexturePresetStatus.Available : TexturePresetStatus.Unsupported,
            missing.Length == 0 ? string.Empty : $"必要な能力がありません: {string.Join(", ", missing)}",
            TrainingStrategyValidator.Hash(strategy));
        _entries.Add(entry);
        return entry;
    }

    private TrainingStrategyRegistration Reject(string origin, string reason)
    {
        _failures.Add((origin, reason));
        return new TrainingStrategyRegistration(false, null, reason);
    }
}
