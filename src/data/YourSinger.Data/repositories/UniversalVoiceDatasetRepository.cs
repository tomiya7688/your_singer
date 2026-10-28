using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class UniversalVoiceDatasetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    // { 責務: [LoadAsync: 保存と排他して共通datasetを読み込む] }
    public async Task<UniversalVoiceDatasetRecord?> LoadAsync(
        ProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(workspace);
        FileStream guard;
        try { guard = await AcquireGuardAsync(path, cancellationToken); }
        catch (DirectoryNotFoundException) { return null; }
        using (guard)
        {
            FileStream stream;
            try { stream = OpenRead(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            await using (stream)
                return await JsonSerializer.DeserializeAsync<UniversalVoiceDatasetRecord>(stream, JsonOptions, cancellationToken);
        }
    }

    // { 責務: [LoadSnapshotAsync: 同じファイル実体からデータと更新確認用ハッシュを読み込む] }
    public async Task<(UniversalVoiceDatasetRecord? Record, string? Revision)> LoadSnapshotAsync(
        ProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(workspace);
        FileStream guard;
        try { guard = await AcquireGuardAsync(path, cancellationToken); }
        catch (DirectoryNotFoundException) { return (null, null); }
        using (guard)
        {
            FileStream stream;
            try { stream = OpenRead(path); }
            catch (FileNotFoundException) { return (null, null); }
            catch (DirectoryNotFoundException) { return (null, null); }
            await using (stream)
            {
                var record = await JsonSerializer.DeserializeAsync<UniversalVoiceDatasetRecord>(stream, JsonOptions, cancellationToken);
                stream.Position = 0;
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                return (record, Convert.ToHexStringLower(hash));
            }
        }
    }

    // { 責務: [SaveAsync: 協調する保存処理と排他してdatasetを保存する] }
    public Task SaveAsync(ProjectWorkspace workspace, UniversalVoiceDatasetRecord record,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(workspace, record, false, null, cancellationToken);

    // { 責務: [SaveIfUnchangedAsync: 読込後の更新がある場合は上書きせず失敗する] }
    public Task SaveIfUnchangedAsync(ProjectWorkspace workspace, UniversalVoiceDatasetRecord record,
        string? expectedRevision, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(workspace, record, true, expectedRevision, cancellationToken);

    // { 責務: [SaveCoreAsync: 排他中に変更を検査し、一時ファイルから保存する] }
    private async Task SaveCoreAsync(ProjectWorkspace workspace, UniversalVoiceDatasetRecord record,
        bool checkRevision, string? expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace.EnsureCreated();
        var path = GetPath(workspace);
        // { 処理: [協調する別プロセスも含め検査から置換まで排他する] }
        using var guard = await AcquireGuardAsync(path, cancellationToken);
        if (checkRevision)
        {
            var current = await CurrentRevisionAsync(path, cancellationToken);
            if (!string.Equals(current, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("共通音声データが別の処理で更新されました。再読み込みしてやり直してください。");
        }
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    // { 責務: [CurrentRevisionAsync: 現在の保存済みデータのハッシュを取得する] }
    private static async Task<string?> CurrentRevisionAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream;
        try { stream = OpenRead(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (stream)
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    // { 責務: [AcquireGuardAsync: ロック競合だけを期限付きで待ち読込と保存を排他する] }
    private static async Task<FileStream> AcquireGuardAsync(string path, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when (IsLockConflict(exception))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                    throw new IOException("共通音声データの処理が続いています。終了してからやり直してください。", exception);
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    // { 責務: [IsLockConflict: WindowsとLinuxの共有違反だけを待機対象とする] }
    private static bool IsLockConflict(IOException exception) =>
        OperatingSystem.IsWindows() ? (exception.HResult & 0xffff) is 32 or 33 :
        OperatingSystem.IsLinux() && (exception.HResult == 11 || exception.HResult == unchecked((int)0x80070020));

    // { 責務: [OpenRead: ファイルを読取専用で開く] }
    private static FileStream OpenRead(string path) => File.OpenRead(path);

    private static string GetPath(ProjectWorkspace workspace) =>
        Path.Combine(workspace.MetadataPath, "universal-voice-dataset.json");
}
