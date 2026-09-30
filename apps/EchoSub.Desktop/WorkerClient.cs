using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EchoSub.Desktop;

public sealed class WorkerClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim requestSlots = new(32, 32);
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private bool disposed;
    private readonly SemaphoreSlim historyGate = new(1, 1);
    public WorkerEventBuffer Events { get; } = new();

    public event Action? Disconnected;

    private WorkerClient(Process process)
    {
        this.process = process;
        stdoutTask = ReadResponsesAsync();
        stderrTask = DrainStderrAsync();
    }

    public static WorkerClient Start(string workerPath, bool enableMockPipeline = false,
        IReadOnlyList<string>? arguments = null)
    {
        if (!Path.IsPathFullyQualified(workerPath) || !File.Exists(workerPath))
        {
            throw new FileNotFoundException("Worker executable was not found", workerPath);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workerPath)!
            }
        };
        if (enableMockPipeline) process.StartInfo.ArgumentList.Add("--mock-pipeline");
        if (arguments is not null)
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
        {
            process.Dispose();
            throw new IOException("Worker process could not be started");
        }
        return new WorkerClient(process);
    }

    public bool IsRunning => !process.HasExited;
    public int ProcessId => process.Id;
    public bool ForcedTerminationUsed { get; private set; }

    public async Task<JsonElement> SendAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (disposed || process.HasExited)
        {
            throw new IOException("Worker is not running");
        }

        await requestSlots.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            var message = JsonSerializer.Serialize(new
            {
                v = 1,
                kind = "command",
                request_id = id,
                method,
                @params = parameters ?? new { }
            });
            if (Encoding.UTF8.GetByteCount(message) > 256 * 1024) throw new IOException("Command exceeds 256 KiB");
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteLineAsync(message);
                await process.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                writeGate.Release();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            return await completion.Task.WaitAsync(linked.Token);
        }
        finally
        {
            pending.TryRemove(id, out _);
            requestSlots.Release();
        }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            await foreach (var line in ReadLinesAsync(process.StandardOutput.BaseStream))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("v").GetInt32() != 1)
                {
                    throw new IOException("Unexpected worker protocol message");
                }
                if (root.GetProperty("kind").GetString() == "event")
                {
                    var sequence = root.GetProperty("seq").GetUInt64();
                    var name = root.GetProperty("event").GetString() ?? throw new IOException("Missing event name");
                    if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 128 || root.GetProperty("payload").ValueKind != JsonValueKind.Object) throw new IOException("Invalid worker event");
                    Events.Publish(new WorkerEvent(sequence, name, root.GetProperty("payload").Clone()));
                    continue;
                }
                if (root.GetProperty("kind").GetString() != "response") throw new IOException("Unexpected worker message kind");
                if (!root.TryGetProperty("request_id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !pending.TryGetValue(idElement.GetString()!, out var completion))
                {
                    continue;
                }
                if (root.GetProperty("ok").GetBoolean())
                {
                    completion.TrySetResult(root.GetProperty("result").Clone());
                }
                else
                {
                    var error = root.GetProperty("error");
                    var code = error.GetProperty("code").GetString();
                    completion.TrySetException(new WorkerException(code ?? "INTERNAL_ERROR"));
                }
            }
        }
        catch (Exception error)
        {
            FailPending(new IOException("Worker protocol stream failed", error));
        }
        finally
        {
            FailPending(new IOException("Worker disconnected"));
            Disconnected?.Invoke();
        }
    }

    private static async IAsyncEnumerable<byte[]> ReadLinesAsync(Stream stream)
    {
        var buffer = new byte[16384];
        using var line = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    yield return line.ToArray();
                    line.SetLength(0);
                }
                else
                {
                    if (line.Length >= 256 * 1024) throw new IOException("Worker message exceeds 256 KiB");
                    line.WriteByte(buffer[i]);
                }
            }
        }
        if (line.Length != 0) throw new IOException("Unterminated worker message");
    }

    public async Task<HistorySnapshot> ReadHistoryAsync(CancellationToken cancellationToken = default)
    {
        await historyGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var rows = new List<HistoryRecord>();
                    ulong? version = null;
                    var offset = 0;
                    while (true)
                    {
                        object parameters = version is null ? new { offset, limit = 4 } : new { offset, limit = 4, expected_version = version.Value };
                        var page = await SendAsync("get_history", parameters, cancellationToken);
                        var returnedVersion = page.GetProperty("history_version").GetUInt64();
                        if (version is not null && version != returnedVersion) throw new IOException("History version changed without rejection");
                        version = returnedVersion;
                        var records = page.GetProperty("records").Deserialize<HistoryRecord[]>() ?? throw new IOException("Missing history records");
                        if (records.Length > 4) throw new IOException("Oversized history page");
                        foreach (var record in records)
                        {
                            if (record.ProductSessionId is not null)
                            {
                                if (record.SessionStartedAtUtc is { } utc && !DateTimeOffset.TryParseExact(utc,
                                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.AssumeUniversal, out _))
                                    throw new IOException("Invalid session UTC timestamp");
                                if (!Guid.TryParseExact(record.ProductSessionId, "D", out _) ||
                                    record.SessionAudioStartSeconds is not double start || !double.IsFinite(start) || start < 0 ||
                                    record.SessionAudioEndSeconds is not double end || !double.IsFinite(end) || end <= start)
                                    throw new IOException("Invalid product session history metadata");
                            }
                            else if (record.SessionAudioStartSeconds is not null || record.SessionAudioEndSeconds is not null)
                                throw new IOException("Session audio time requires UUID identity");
                            if (record.Source is null || record.Translation is null || Encoding.UTF8.GetByteCount(record.Source) > 4096 || Encoding.UTF8.GetByteCount(record.Translation) > 4096 || record.Source.Contains('\0') || record.Translation.Contains('\0') || record.AudioStartSample >= record.AudioEndSample || record.AudioEndSample - record.AudioStartSample > 128000 || record.SourceRevision == 0 || record.SegmentId == 0 || !double.IsFinite(record.AudioStartSeconds) || !double.IsFinite(record.AudioEndSeconds)) throw new IOException("Invalid history record");
                            if (record.SourceState is not ("Partial" or "FinalPending" or "Final" or "Failed" or "Skipped" or "Discarded") || record.TranslationState is not ("None" or "Pending" or "Done" or "Failed" or "Skipped" or "Bypassed") || record.AppliedSourceRevision > record.SourceRevision) throw new IOException("Invalid history state");
                            if (record.SourceState == "Final" && (record.AppliedSourceRevision != record.SourceRevision || string.IsNullOrWhiteSpace(record.Source))) throw new IOException("Invalid final source");
                        }
                        rows.AddRange(records);
                        if (rows.Count > 1000) throw new IOException("History exceeds 1000 records");
                        var next = page.GetProperty("next_offset");
                        if (next.ValueKind == JsonValueKind.Null)
                        {
                            var sequence = page.GetProperty("last_seq").GetUInt64();
                            Events.AcceptSnapshot(sequence);
                            return new HistorySnapshot(version.Value, sequence, rows.AsReadOnly());
                        }
                        var nextOffset = next.GetInt32();
                        if (nextOffset != offset + records.Length || nextOffset <= offset || nextOffset > 1000) throw new IOException("Invalid history page cursor");
                        offset = nextOffset;
                    }
                }
                catch (WorkerException error) when (error.Code == "STALE_SNAPSHOT") { }
            }
            throw new WorkerException("SNAPSHOT_BUSY");
        }
        finally { historyGate.Release(); }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is not null)
            {
                // The scaffold keeps diagnostics out of stdout and does not retain raw lines.
            }
        }
        catch (IOException)
        {
            // Process shutdown can close the pipe while the reader is waiting.
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var item in pending.Values)
        {
            item.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        if (!process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendAsync("shutdown", cancellationToken: timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception error) when (error is IOException or WorkerException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        ForcedTerminationUsed = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // The worker exited between HasExited and Kill.
                    }
                    await process.WaitForExitAsync();
                }
            }
        }
        disposed = true;
        process.StandardInput.Dispose();
        await Task.WhenAll(stdoutTask, stderrTask);
        process.Dispose();
        writeGate.Dispose();
        requestSlots.Dispose();
        historyGate.Dispose();
    }
}

public sealed class WorkerException(string code) : Exception($"Worker error: {code}")
{
    public string Code { get; } = code;
}

