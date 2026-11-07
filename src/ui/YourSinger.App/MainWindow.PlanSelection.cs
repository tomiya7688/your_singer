using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.App;

public sealed partial class MainWindow
{
    private const string NoTextureChoice = "指定しない(既定: 自然)";
    private IReadOnlyList<TextureChoice> _talkTextures = [];
    private IReadOnlyList<TextureChoice> _singingTextures = [];

    // {
    //   責務: [InitializePlanSelection: 質感の選択肢と初期化モードの選択肢を画面へ入れる]
    //   処理: [1: 登録簿から用途別の質感を取り出す 2: 先頭に「指定しない」を置く 3: 初期化モードを自動・追加学習・最初から新規学習にする]
    //   副作用: [TalkTextureCombo/SingingTextureCombo/TrainingModeComboの項目と既定選択を設定する]
    // }
    private void InitializePlanSelection()
    {
        var registry = new TexturePresetRegistry();
        _talkTextures = TrainingPlanSelection.Choices(registry, "talk");
        _singingTextures = TrainingPlanSelection.Choices(registry, "singing");
        TalkTextureCombo.ItemsSource = _talkTextures.Select(x => x.Label).Prepend(NoTextureChoice).ToArray();
        SingingTextureCombo.ItemsSource = _singingTextures.Select(x => x.Label).Prepend(NoTextureChoice).ToArray();
        TalkTextureCombo.SelectedIndex = 0;
        SingingTextureCombo.SelectedIndex = 0;
        TrainingModeCombo.ItemsSource = new[] { TrainingMode.Auto, TrainingMode.FineTune, TrainingMode.FromScratch }
            .Select(TrainingPlanSelection.ModeLabel).ToArray();
        TrainingModeCombo.SelectedIndex = 0;
    }

    // { 責務: [OnTextureChanged: 選んだ質感の説明(実現できない場合はその理由を含む)を表示する] }
    private void OnTextureChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo)
            return;
        var choices = combo == TalkTextureCombo ? _talkTextures : _singingTextures;
        var choice = SelectedTexture(combo, choices);
        TextureDescriptionText.Text = choice?.Description
            ?? "質感を選ばない場合は「自然」の既定で作成します。選んだ質感は別の方式へ黙って変えず、実現できない場合は理由を表示します。";
    }

    // {
    //   責務: [BuildPlanOptions: 画面の質感選択と初期化モードを、ジョブ作成の用途別設定にする]
    //   引数: [combo: トークまたは歌唱の質感選択]
    //   戻り値: [options: 既定のままなら null]
    // }
    private TrainingJobOptions? BuildPlanOptions(ComboBox combo)
    {
        var choices = combo == TalkTextureCombo ? _talkTextures : _singingTextures;
        var mode = TrainingModeCombo.SelectedIndex switch
        {
            1 => TrainingMode.FineTune,
            2 => TrainingMode.FromScratch,
            _ => TrainingMode.Auto
        };
        return TrainingPlanSelection.BuildOptions(SelectedTexture(combo, choices), mode);
    }

    // 先頭は「指定しない」なので、選択位置は選択肢より1つ多い。
    private static TextureChoice? SelectedTexture(ComboBox combo, IReadOnlyList<TextureChoice> choices) =>
        combo.SelectedIndex > 0 && combo.SelectedIndex - 1 < choices.Count ? choices[combo.SelectedIndex - 1] : null;
}
