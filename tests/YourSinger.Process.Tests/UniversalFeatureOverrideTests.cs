using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Tests;

public sealed class UniversalFeatureOverrideTests
{
    [Fact]
    public async Task TranscriptEditInvalidatesPreviouslyGeneratedPhoneTimings()
    {
        using var project = new TestProject();
        var segment = project.Segment("edited");
        segment.AlignmentMethod = "legacy-uniform-placeholder";
        await project.SeedAsync(segment);

        await new UniversalFeatureOverrideService(project.DatasetRepository)
            .SetTranscriptAsync(project.Workspace, "edited", "修正した歌詞", TestProject.Token);

        var dataset = await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token);
        var updated = Assert.Single(dataset!.Segments);
        Assert.Equal("修正した歌詞", updated.Transcript);
        Assert.Empty(updated.Phonemes);
        Assert.Equal(0, updated.AlignmentConfidence);
        Assert.Equal("invalidated-by-transcript-override", updated.AlignmentMethod);
    }

    // {
    //   責務: [ReanalysisUsesLatestTranscriptAndPreservesNewTimings: 最終修正による再解析とキャッシュを確認する]
    //   処理: [1: 複数の修正履歴を保存する 2: 最終歌詞で再解析する 3: 再利用と修正時の再抽出を確認する]
    //   エラー: [ワーカー要求・再解析結果・キャッシュが不正な場合に失敗する]
    // }
    [Fact]
    public async Task ReanalysisUsesLatestTranscriptAndPreservesNewTimings()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("edited"));
        var edits = new UniversalFeatureOverrideService(project.DatasetRepository);
        await edits.SetTranscriptAsync(project.Workspace, "edited", "最初の修正", TestProject.Token);
        await edits.SetTranscriptAsync(project.Workspace, "edited", "最終の修正", TestProject.Token);
        var source = new SourceRecord
        {
            SourceId = "src_a", Path = Path.Combine(project.Workspace.SegmentsPath, "edited.wav"),
            MediaType = MediaType.Audio, ContentHash = "fixture", AnalysisVersion = "fixture"
        };
        var service = CreateFeatureService(project);
        var first = Assert.Single((await service.BuildAsync(project.Workspace, [source], TestProject.Token)).Segments);
        Assert.Equal("最終の修正", first.Transcript);
        Assert.Single(first.Phonemes);
        Assert.Equal(UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod, first.AlignmentMethod);

        // 同じ歌詞はキャッシュを使い、変更した歌詞は別の入力として再抽出する。
        await service.BuildAsync(project.Workspace, [source], TestProject.Token);
        var callsPath = Path.Combine(project.Workspace.RootPath, "feature-calls.txt");
        Assert.Single(await File.ReadAllLinesAsync(callsPath, TestProject.Token));
        await edits.SetTranscriptAsync(project.Workspace, "edited", "変更後の歌詞", TestProject.Token);
        var changed = Assert.Single((await service.BuildAsync(project.Workspace, [source], TestProject.Token)).Segments);
        Assert.Equal("変更後の歌詞", changed.Transcript);
        Assert.Single(changed.Phonemes);
        Assert.NotEqual(first.CacheKey, changed.CacheKey);
        Assert.Equal(2, (await File.ReadAllLinesAsync(callsPath, TestProject.Token)).Length);
    }

    // {
    //   責務: [CreateFeatureService: 要求された修正歌詞を返す合成ワーカーでサービスを組み立てる]
    //   処理: [1: IPC経由の要求と呼出回数を記録する 2: 架空の実測形式を返す]
    //   引数: [project: 合成素材とリポジトリ]
    //   戻り値: [service: 実モデルを使わない再解析サービス]
    // }
    private static UniversalVoiceDatasetService CreateFeatureService(TestProject project)
    {
        var python = Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python";
        var code = """
            import json,sys
            from pathlib import Path
            request=json.loads(sys.stdin.readline()); payload=request['payload']
            assert isinstance(payload['transcript_override'],str)
            with (Path(payload['workspace_path'])/'feature-calls.txt').open('a',encoding='utf-8') as calls:
                calls.write('called\n')
            feature=dict(segment_id=payload['segment_id'],source_id=payload['source_id'],audio_path=payload['audio_path'],
                cache_key=payload['cache_key'],transcript=payload['transcript_override'],asr_confidence=0.95,
                phonemes=[dict(phoneme='a',start_sec=0.0,end_sec=0.1,confidence=0.95)],alignment_confidence=0.95,
                alignment_method='mfa-japanese-phone-forced-alignment-v1',f0_feature_path='features/edited-f0.json',
                energy_feature_path='features/edited-energy.json',voicing_feature_path='features/edited-voicing.json')
            print(json.dumps(dict(request_id=request['request_id'],status='ok',result=feature),ensure_ascii=False))
            """;
        return new UniversalVoiceDatasetService(new MlWorkerClient(python, ["-u", "-c", code]),
            new AudioPreprocessingRepository(), project.Speakers, project.Classifications,
            project.DatasetRepository, new FeatureCacheKeyService());
    }
}
