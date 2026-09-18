using System.Text.Json;

namespace YourSinger.Process.Processing.Ml.Protocol;

public sealed record MlWorkerRequest(
    string RequestId,
    string Command,
    JsonElement Payload);

public sealed record MlWorkerResponse(
    string RequestId,
    string Status,
    JsonElement? Result,
    string? Error);
