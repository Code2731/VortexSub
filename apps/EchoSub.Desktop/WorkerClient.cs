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

    public event Action? Disconnected;

    private WorkerClient(Process process)
    {
        this.process = process;
        stdoutTask = ReadResponsesAsync();
        stderrTask = DrainStderrAsync();
    }

    public static WorkerClient Start(string workerPath)
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
        if (!process.Start())
        {
            process.Dispose();
            throw new IOException("Worker process could not be started");
        }
        return new WorkerClient(process);
    }

    public bool IsRunning => !process.HasExited;
    public int ProcessId => process.Id;

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
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("v").GetInt32() != 1 ||
                    root.GetProperty("kind").GetString() != "response")
                {
                    throw new IOException("Unexpected worker protocol message");
                }
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
    }
}

public sealed class WorkerException(string code) : Exception($"Worker error: {code}")
{
    public string Code { get; } = code;
}

