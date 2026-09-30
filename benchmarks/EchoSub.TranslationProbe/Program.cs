using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    if (args.Length != 4 && args.Length != 8) throw new ArgumentException("Usage: <http://127.0.0.1:PORT/v1/> <model-id or -> <fixtures.json> <report.json>");
    var endpoint = new Uri(args[0]);
    if (endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
        !(endpoint.Host == "localhost" || IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))
        throw new ArgumentException("The diagnostic probe only sends authored fixtures to a local loopback HTTP endpoint.");
    if (!endpoint.AbsolutePath.EndsWith('/')) throw new ArgumentException("Base URL must end with '/'.");
    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
    using var client = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(120), MaxResponseContentBufferSize = 1024 * 1024 };
    var token = Environment.GetEnvironmentVariable("ECHOSUB_TRANSLATION_TOKEN");
    if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var modelResponse = await client.GetAsync("models");
    modelResponse.EnsureSuccessStatusCode();
    using var modelJson = JsonDocument.Parse(await modelResponse.Content.ReadAsStringAsync());
    var modelIds = modelJson.RootElement.GetProperty("data").EnumerateArray().Select(model => model.GetProperty("id").GetString()!).ToArray();
    var modelId = args[1] == "-" && modelIds.Length == 1 ? modelIds[0] : args[1];
    if (!modelIds.Contains(modelId)) throw new InvalidOperationException("Requested model ID is not present in /v1/models; specify an actual listed ID.");
    if (new FileInfo(args[2]).Length > 1024 * 1024) throw new InvalidOperationException("Fixture manifest exceeds 1 MiB.");
    var bytes = await File.ReadAllBytesAsync(args[2]);
    if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Fixture manifest exceeds 1 MiB.");
    using var corpus = JsonDocument.Parse(bytes);
    if (corpus.RootElement.GetProperty("schema_version").GetInt32() != 1) throw new InvalidOperationException("Unsupported fixture schema.");
    var cases = corpus.RootElement.GetProperty("cases");
    if (cases.GetArrayLength() is < 1 or > 100) throw new InvalidOperationException("Expected 1..100 translation cases.");
    var ids = new HashSet<string>();
    foreach (var item in cases.EnumerateArray())
    {
        var id = item.GetProperty("id").GetString();
        var source = item.GetProperty("source").GetString();
        var language = item.GetProperty("language").GetString();
        var reference = item.GetProperty("reference").GetString();
        if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(source) || source.Length > 2000 ||
            string.IsNullOrWhiteSpace(reference) || language is not ("en" or "ja")) throw new InvalidOperationException("Invalid or duplicate translation fixture.");
    }
    var loadMode = args.Length == 8;
    var durationS = loadMode ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0;
    var intervalS = loadMode ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0;
    if (loadMode && (!double.IsFinite(durationS) || durationS is < 1 or > 3600 || !double.IsFinite(intervalS) || intervalS is < 0.05 or > 10))
        throw new ArgumentException("Load duration must be 1..3600 s, interval 0.05..10 s.");
    var results = new List<object>();
    var fixtureItems = cases.EnumerateArray().ToArray();
    var offered = loadMode ? (int)Math.Ceiling(durationS / intervalS) : fixtureItems.Length;
    var skipped = 0;
    var next = 0;
    var clock = new Stopwatch();
    double? scheduledStartUtcS = null, actualStartUtcS = null;
    if (loadMode)
    {
        await Translate(fixtureItems[0].GetProperty("source").GetString()!); // Excluded warm-up.
        if (File.Exists(args[6]) || File.Exists(args[7])) throw new InvalidOperationException("Load control files must be new.");
        await File.WriteAllTextAsync(args[6], "ready");
        var gateWait = Stopwatch.StartNew();
        while (!File.Exists(args[7]))
        {
            if (gateWait.Elapsed.TotalSeconds > 120) throw new TimeoutException("Start gate timed out.");
            await Task.Delay(20);
        }
        scheduledStartUtcS = double.Parse(await File.ReadAllTextAsync(args[7]), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(scheduledStartUtcS.Value) || Math.Abs(scheduledStartUtcS.Value - UtcS()) > 30) throw new InvalidOperationException("Invalid gate time.");
        while (UtcS() < scheduledStartUtcS) await Task.Delay(1);
        actualStartUtcS = UtcS();
        clock.Start();
    }
    else clock.Start();
    while (next < offered && (!loadMode || clock.Elapsed.TotalSeconds < durationS))
    {
        var due = loadMode ? (int)(clock.Elapsed.TotalSeconds / intervalS) : next;
        if (due >= offered) break;
        if (due < next) { await Task.Delay(1); continue; }
        skipped += due - next;
        next = due + 1;
        var item = fixtureItems[due % fixtureItems.Length];
        var id = item.GetProperty("id").GetString();
        var source = item.GetProperty("source").GetString();
        var language = item.GetProperty("language").GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(source) || source.Length > 2000 || language is not ("en" or "ja")) throw new InvalidOperationException("Invalid translation fixture.");
        var timer = Stopwatch.StartNew();
        var beginS = clock.Elapsed.TotalSeconds;
        JsonElement? completion = null;
        string? error = null;
        try { completion = await Translate(source!); }
        catch (Exception failure) when (loadMode) { error = failure.ToString(); }
        timer.Stop();
        var choice = completion?.GetProperty("choices")[0];
        var translation = choice?.GetProperty("message").GetProperty("content").GetString();
        results.Add(new { id, language, source, reference = item.GetProperty("reference").GetString(), translation, elapsed_s = timer.Elapsed.TotalSeconds,
            arrival_index = due, scheduled_s = loadMode ? due * intervalS : 0, start_s = beginS, finish_s = clock.Elapsed.TotalSeconds,
            start_lag_s = loadMode ? beginS - due * intervalS : 0, error,
            finish_reason = choice?.GetProperty("finish_reason").GetString(), usage = completion is { } completed && completed.TryGetProperty("usage", out var usage) ? usage.Clone() : (JsonElement?)null,
            manual_review = "PENDING: meaning, numbers, negation, added content" });
        if (!loadMode || results.Count % 100 == 0) Console.WriteLine($"{id}: {timer.Elapsed.TotalSeconds:F6} s; {results.Count} finished, {skipped} skipped; {clock.Elapsed.TotalSeconds:F1} s");
    }
    skipped += offered - next;
    while (loadMode && clock.Elapsed.TotalSeconds < durationS) await Task.Delay(1);
    if (results.Count == 0) throw new InvalidOperationException("No translation cases.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(new { task = loadMode ? "T00-04.4" : "T00-04.1", os = Environment.OSVersion.ToString(), model_id = modelId,
        fixture_sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), available_model_ids = modelIds,
        cold_first_request_included = !loadMode, quality_gate_passed = false,
        duration_s = durationS, actual_wall_s = clock.Elapsed.TotalSeconds, scheduled_start_utc_s = scheduledStartUtcS,
        actual_start_utc_s = actualStartUtcS, actual_end_utc_s = UtcS(), interval_s = intervalS, offered, skipped, pending_capacity = 0,
        policy = "one running request; skip expired arrivals; fixture selected by arrival index", results = loadMode ? null : results, runs = loadMode ? results : null }, new JsonSerializerOptions { WriteIndented = true }));

    static double UtcS() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    async Task<JsonElement> Translate(string source)
    {
        using var response = await client.PostAsJsonAsync("chat/completions", new
        {
            model = modelId, stream = false, temperature = 0, max_tokens = 256,
            messages = new[]
            {
                new { role = "system", content = "Translate the user's game dialogue into Korean. Preserve numbers, negation, and meaning. Output only the Korean translation, without explanation or additional information." },
                new { role = "user", content = source }
            }
        });
        response.EnsureSuccessStatusCode();
        using var completion = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (string.IsNullOrWhiteSpace(completion.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()))
            throw new InvalidOperationException("Empty translation.");
        return completion.RootElement.Clone();
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}

