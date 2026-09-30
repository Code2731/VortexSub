using System.Diagnostics;
using EchoSub.Desktop;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EchoSub.ProtocolSmoke <absolute-worker-path>");
    return 2;
}

var workerPath = Path.GetFullPath(args[0]);

await using (var client = WorkerClient.Start(workerPath))
{
    var processId = client.ProcessId;
    var hello = await client.SendAsync("hello", new { client = "ProtocolSmoke", protocol_major = 1 });
    Require(hello.GetProperty("implementation").GetString() == "mock", "mock capability");
    Require(!hello.GetProperty("capabilities").GetProperty("system_audio").GetBoolean(), "system audio must be unavailable");

    var nonce = "한국어 日本語 😊\nnext";
    var ping = await client.SendAsync("ping", new { nonce });
    Require(ping.GetProperty("nonce").GetString() == nonce, "Unicode/newline round trip");

    var state = await client.SendAsync("get_state");
    Require(state.GetProperty("session").GetProperty("state").GetString() == "Idle", "initial state");

    try
    {
        await client.SendAsync("start_session");
        throw new Exception("Unsupported start_session unexpectedly succeeded");
    }
    catch (WorkerException error) when (error.Code == "UNSUPPORTED_CAPABILITY")
    {
    }

    var calls = Enumerable.Range(0, 64)
        .Select(index => client.SendAsync("ping", new { nonce = index.ToString() }))
        .ToArray();
    var responses = await Task.WhenAll(calls);
    for (var index = 0; index < responses.Length; index++)
    {
        Require(responses[index].GetProperty("nonce").GetString() == index.ToString(), "request response matching");
    }
    await client.DisposeAsync();
    Require(!IsAlive(processId), "normal shutdown left worker alive");
}

await using (var client = WorkerClient.Start(workerPath))
{
    await client.SendAsync("hello", new { client = "ProtocolSmoke", protocol_major = 1 });
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.Disconnected += () => disconnected.TrySetResult();
    using (var process = Process.GetProcessById(client.ProcessId))
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    try
    {
        await client.SendAsync("ping", new { nonce = "after-kill" });
        throw new Exception("Killed worker unexpectedly accepted a request");
    }
    catch (IOException)
    {
    }
}

Console.WriteLine("PASS: C# client ↔ Rust worker, Unicode, 64 requests, shutdown, worker death");
return 0;

static void Require(bool condition, string label)
{
    if (!condition) throw new Exception($"FAILED: {label}");
}

static bool IsAlive(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

