using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EchoSub.Desktop;

// Offline diagnostic only. No capture, playback, or rendered UI.
internal static class StreamingTranslationProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task Sources(string[] args)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(args[4]));
        await using var client = WorkerClient.Start(args[1], arguments: new[] { "--diagnostic-asr", "--asr-model", args[2], "--asr-sha256", args[3], "--asr-backend", "cpu" });
        await client.SendAsync("hello", new { protocol_major = 1, client = "StreamingSourceProbe" });
        await WaitState(client, s => s.GetProperty("model").GetProperty("state").GetString() == "Ready");
        // Exclude first decode warmup from the generated availability estimates.
        var first = manifest.RootElement.GetProperty("cases")[0].GetProperty("steps")[0];
        await Decode(first);
        var cases = new List<object>();
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var steps = new List<object>();
            double previousAvailable = 0;
            foreach (var step in item.GetProperty("steps").EnumerateArray())
            {
                var (record, elapsed) = await Decode(step);
                var available = Math.Max(previousAvailable + 0.01, step.GetProperty("audio_end_s").GetDouble() + elapsed);
                previousAvailable = available;
                bool final = step.GetProperty("final").GetBoolean();
                if (string.IsNullOrWhiteSpace(record.Source))
                {
                    if (final) throw new IOException("Final ASR text is empty: " + item.GetProperty("id"));
                    continue;
                }
                steps.Add(new { available_s = available, source = record.Source, final,
                    audio_end_s = step.GetProperty("audio_end_s").GetDouble(), file_to_source_s = elapsed,
                    source_state = record.SourceState, wav_sha256 = step.GetProperty("sha256").GetString() });
            }
            cases.Add(new { id = item.GetProperty("id").GetString(), language = "en", steps });
            Save();
        }
        void Save() => File.WriteAllText(args[5], JsonSerializer.Serialize(new { note = "Actual CPU Whisper on independent growing file prefixes. Availability = audio end + measured file-to-source, monotonically adjusted. Precomputed replay, not live scheduler latency.", asr_backend = "cpu", asr_model_sha256 = args[3], fixture_sha256 = Hash(args[4]), capture = false, cases }, JsonOptions));
        async Task<(HistoryRecord Record, double Elapsed)> Decode(JsonElement step)
        {
            var timer = Stopwatch.StartNew();
            var accepted = await client.SendAsync("transcribe_fixture", new { path = step.GetProperty("path").GetString(), sha256 = step.GetProperty("sha256").GetString(), language = "en" });
            var segment = accepted.GetProperty("segment_id").GetUInt64();
            while (timer.Elapsed.TotalSeconds < 15)
            {
                var record = (await client.ReadHistoryAsync()).Records.SingleOrDefault(r => r.SegmentId == segment);
                if (record is { SourceState: "Final" or "Skipped" or "Failed" })
                {
                    if (record.SourceState == "Failed") throw new IOException("Prefix ASR failed");
                    return (record, timer.Elapsed.TotalSeconds);
                }
                await Task.Delay(10);
            }
            throw new IOException("Prefix ASR did not complete");
        }
    }

    public static async Task Replay(string[] args)
    {
        if (new FileInfo(args[4]).Length > 1024 * 1024) throw new IOException("Trace exceeds 1 MiB");
        using var trace = JsonDocument.Parse(File.ReadAllText(args[4]));
        var profile = args.Length == 7 ? args[6] : "existing";
        if (profile is not ("existing" or "qwen-greedy" or "hymt2-greedy")) throw new IOException("Invalid replay profile");
        var cases = trace.RootElement.GetProperty("cases");
        if (cases.GetArrayLength() is < 1 or > 100) throw new IOException("Expected 1..100 trace cases");
        foreach (var item in cases.EnumerateArray())
        {
            var steps = item.GetProperty("steps");
            if (steps.GetArrayLength() is < 1 or > 100) throw new IOException("Expected 1..100 revisions");
            double previous = -1;
            int index = 0;
            foreach (var step in steps.EnumerateArray())
            {
                var time = step.GetProperty("available_s").GetDouble();
                var source = step.GetProperty("source").GetString();
                if (!double.IsFinite(time) || time < 0 || time > 60 || time <= previous
                    || string.IsNullOrWhiteSpace(source) || System.Text.Encoding.UTF8.GetByteCount(source) > 4096
                    || step.GetProperty("final").GetBoolean() != (++index == steps.GetArrayLength()))
                    throw new IOException("Invalid paced source revision");
                previous = time;
            }
        }
        var runs = new List<object>();
        // Reverse mode order for alternate cases to reduce simple order bias.
        int caseIndex = 0;
        foreach (var item in trace.RootElement.GetProperty("cases").EnumerateArray())
        {
            foreach (bool preview in caseIndex++ % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var options = new List<string> { "--mock-pipeline", "--mock-session-control", "--diagnostic-translation" };
                await using var client = WorkerClient.Start(args[1], arguments: options);
                await client.SendAsync("hello", new { protocol_major = 1, client = "StreamingTranslationReplay" });
                await client.SendAsync("configure_translation", new { endpoint = args[2], model_id = args[3],
                    input_profile = profile == "existing" ? "standard" : profile });
                await WaitState(client, s => s.GetProperty("translator").GetProperty("state").GetString() == "Ready");
                var appliedTranslator = (await client.SendAsync("get_state", new { })).GetProperty("translator");
                var expectedProfile = profile == "existing" ? "standard" : profile;
                if (appliedTranslator.GetProperty("input_profile").GetString() != expectedProfile
                    || appliedTranslator.GetProperty("model_id").GetString() != args[3])
                    throw new IOException("Applied translation model/profile does not match replay configuration");
                var started = await client.SendAsync("start_session", new { history_policy = "retain", config = new { source_language = item.GetProperty("language").GetString(), partial_enabled = true, partial_translation_enabled = preview } });
                var uuid = started.GetProperty("session_id").GetString();
                await WaitState(client, s => s.GetProperty("session").GetProperty("state").GetString() == "Running");
                var clock = Stopwatch.StartNew();
                var deck = new CaptionDeck();
                var cosmeticDeck = new CaptionDeck { StabilizeCosmeticRevisions = true };
                var cosmeticObservations = new List<object>();
                var deckSnapshots = new List<object>();
                var cosmeticSnapshots = new List<object>();
                CaptionCards lastCards = new(null, null), lastCosmeticCards = new(null, null);
                double? firstCosmeticApplied = null;
                cosmeticDeck.TargetObserved += observation =>
                {
                    if (observation.Phase == "Displayed") firstCosmeticApplied ??= observation.AtSeconds;
                    cosmeticObservations.Add(new { phase = observation.Phase, slot = observation.Slot,
                        at_s = observation.AtSeconds, change = observation.Change,
                        not_before_s = observation.NotBeforeSeconds,
                        translation_request_id = observation.Record.TranslationRequestId });
                };
                var targetObservations = new List<object>();
                double? firstDeckApplied = null;
                deck.TargetObserved += observation =>
                {
                    if (observation.Phase == "Displayed") firstDeckApplied ??= observation.AtSeconds;
                    targetObservations.Add(new { phase = observation.Phase, slot = observation.Slot,
                        at_s = observation.AtSeconds, change = observation.Change,
                        source_revision = observation.Record.SourceRevision,
                        translation_request_id = observation.Record.TranslationRequestId,
                        semantic_contradiction = "UNASSESSED" });
                };
                var snapshots = new List<object>();
                var seen = new HashSet<ulong>();
                double? firstTranslation = null, firstPreview = null, finalTranslation = null;
                double finalSubmitted = 0;
                HistoryRecord? finalRecord = null;
                var outputs = new List<string>();
                var arrivals = new List<object>();
                int removedCharacters = 0, appendedCharacters = 0, finalRemoved = 0;
                foreach (var step in item.GetProperty("steps").EnumerateArray())
                {
                    var available = step.GetProperty("available_s").GetDouble();
                    while (clock.Elapsed.TotalSeconds < available) { await Observe(); await Task.Delay(10); }
                    var final = step.GetProperty("final").GetBoolean();
                    await client.SendAsync("mock_segment", new { source = step.GetProperty("source").GetString(), kind = final ? "final" : "partial" });
                    arrivals.Add(new { scheduled_s = available, submitted_s = clock.Elapsed.TotalSeconds,
                        final, source = step.GetProperty("source").GetString() });
                    if (final) finalSubmitted = clock.Elapsed.TotalSeconds;
                    await Observe();
                }
                while (finalTranslation is null && clock.Elapsed.TotalSeconds - finalSubmitted < 10)
                {
                    await Observe(); await Task.Delay(10);
                }
                if (finalTranslation is null)
                {
                    var failureState = await client.SendAsync("get_state");
                    var failureHistory = await client.ReadHistoryAsync();
                    File.WriteAllText(args[5] + ".failure.json", JsonSerializer.Serialize(new {
                        id = item.GetProperty("id").GetString(), preview, failureState, failureHistory, snapshots
                    }, JsonOptions));
                    throw new IOException("Final replay translation failed to complete: " + item.GetProperty("id").GetString());
                }
                var state = await client.SendAsync("get_state");
                runs.Add(new { id = item.GetProperty("id").GetString(), preview_enabled = preview,
                    language = item.GetProperty("language").GetString(),
                    source_kind = item.TryGetProperty("source_kind", out var kind) ? kind.GetString() : "unspecified",
                    first_translation_s = firstTranslation, first_preview_s = firstPreview,
                    final_submitted_s = finalSubmitted, final_translation_s = finalTranslation,
                    final_http_and_ipc_s = finalTranslation - finalSubmitted,
                    translated_updates = outputs.Count, changed_translation_updates = outputs.Zip(outputs.Skip(1)).Count(p => p.First != p.Second),
                    removed_characters = removedCharacters, appended_characters = appendedCharacters,
                    final_correction_removed_characters = finalRemoved, arrivals,
                    first_deck_applied_s = firstDeckApplied, target_observations = targetObservations,
                    cosmetic_policy = new { first_deck_applied_s = firstCosmeticApplied,
                        first_deck_delta_s = firstCosmeticApplied - firstDeckApplied,
                        target_observations = cosmeticObservations, snapshots = cosmeticSnapshots },
                    deck_snapshots = deckSnapshots,
                    http_completed = state.GetProperty("translator").GetProperty("completed_jobs").GetUInt64(),
                    preview_http_completed = state.GetProperty("translator").GetProperty("preview_completed_jobs").GetUInt64(),
                    final_record = finalRecord, snapshots, semantic_review = "PENDING" });
                Save(false);
                Console.WriteLine($"{item.GetProperty("id").GetString()} preview={preview}: first={firstTranslation:F3}s final={finalTranslation:F3}s updates={outputs.Count}");
                async Task Observe()
                {
                    var history = await client.ReadHistoryAsync();
                    var record = history.Records.LastOrDefault(r => r.ProductSessionId == uuid);
                    if (record is not null)
                    {
                        var at = clock.Elapsed.TotalSeconds;
                        var cards = deck.Update(history.Records, uuid, record.SessionId, record.Epoch, true, at);
                        var cosmeticCards = cosmeticDeck.Update(history.Records, uuid, record.SessionId, record.Epoch, true, at);
                        if (cards.Previous?.Translation != lastCards.Previous?.Translation || cards.Current?.Translation != lastCards.Current?.Translation)
                            deckSnapshots.Add(new { at_s = at, cards });
                        if (cosmeticCards.Previous?.Translation != lastCosmeticCards.Previous?.Translation || cosmeticCards.Current?.Translation != lastCosmeticCards.Current?.Translation)
                            cosmeticSnapshots.Add(new { at_s = at, cards = cosmeticCards });
                        lastCards = cards; lastCosmeticCards = cosmeticCards;
                    }
                    if (record is not { TranslationState: "Done", TranslationRequestId: > 0 } || record.AppliedSourceRevision != record.SourceRevision || !seen.Add(record.TranslationRequestId.Value)) return;
                    var time = clock.Elapsed.TotalSeconds;
                    firstTranslation ??= time;
                    var prior = outputs.LastOrDefault() ?? "";
                    int shared = 0;
                    while (shared < prior.Length && shared < record.Translation.Length && prior[shared] == record.Translation[shared]) shared++;
                    var removed = prior.Length - shared;
                    var appended = record.Translation.Length - shared;
                    removedCharacters += removed; appendedCharacters += appended;
                    if (!record.TranslationIsPreview) finalRemoved = removed;
                    outputs.Add(record.Translation);
                    if (record.TranslationIsPreview) firstPreview ??= time;
                    else if (record.SourceState == "Final") { finalTranslation = time; finalRecord = record; }
                    snapshots.Add(new { received_s = time, removed_characters = removed,
                        appended_characters = appended, record });
                }
            }
        }
        Save(true);
        void Save(bool passed) => File.WriteAllText(args[5], JsonSerializer.Serialize(new { passed,
            note = "Source revisions replayed through MOCK ASR admission; actual local HTTP translation and production pipeline. Native prefixes, when supplied, were decoded beforehand. Not a live audio/UI latency measurement.",
            capture = false, rendered_ui = false, quality_gate_passed = false,
            source_trace_sha256 = Hash(args[4]), endpoint = args[2], model_id = args[3], profile,
            revision_metric = "UTF-16 characters replaced after longest common prefix; initial append included, removal excludes initial output; not semantic error count or rendered overlay churn",
            deck_scope = "Actual two-card presenters replayed without Avalonia or screen rendering; Update/Tick driven by this diagnostic polling clock, not real UI cadence",
            runs }, JsonOptions));
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task WaitState(WorkerClient client, Func<JsonElement, bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 15)
        {
            var state = await client.SendAsync("get_state");
            if (predicate(state)) return;
            await Task.Delay(10);
        }
        throw new IOException("Diagnostic preparation did not complete");
    }
}
