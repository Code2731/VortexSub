using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EchoSub.Desktop;

internal static class NativeVadSmoke
{
    public static async Task Run(string[] args)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(args[6]));
        var entries = assets.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var model = entries.Single(x => x.GetProperty("id").GetString() == "silero-v6");
        var runtime = entries.Single(x => x.GetProperty("id").GetString() == "ort-win-x64");
        var root = Path.GetDirectoryName(args[6])!;
        string AssetPath(JsonElement entry) => Path.GetFullPath(Path.Combine(root, entry.GetProperty("path").GetString()!));
        var options = new[] { "--diagnostic-asr", "--asr-model", args[1], "--asr-sha256", args[2], "--asr-backend", args[4],
            "--diagnostic-vad", "--vad-model", AssetPath(model), "--vad-sha256", model.GetProperty("sha256").GetString()!,
            "--vad-runtime", AssetPath(runtime), "--vad-runtime-sha256", runtime.GetProperty("sha256").GetString()! };
        using var corpus = JsonDocument.Parse(File.ReadAllText(args[3]));
        var fixtures = corpus.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        var fixtureRoot = Path.GetDirectoryName(args[3])!;
        int finals = 0, failed = 0, calls = 0, suppressed = 0;
        int skipped = 0;
        var decodeDiagnostics = new List<JsonElement>();
        var vadSeconds = new List<double>();
        var pingMax = 0.0;
        double controlledSilentAudio = 0;
        int controlledSilentRounds = 0;
        var outputDirectory = Path.GetDirectoryName(args[5])!;
        var baselineRanges = new List<(double, double)>();
        await using var client = WorkerClient.Start(args[0], arguments: options);
        var hello = await client.SendAsync("hello", new { protocol_major = 1, client = "NativeVadSmoke" });
        Require(hello.GetProperty("capabilities").GetProperty("vad").GetBoolean(), "VAD capability");
        Require(hello.GetProperty("capabilities").GetProperty("source_token_alignment").GetBoolean(), "Native token alignment capability");
        await Event("model.state");
        // File PCM bypasses system loopback and cannot contain unrelated playback.
        var controlledSilence = Path.Combine(outputDirectory, "controlled-silence-8s.wav");
        Save(controlledSilence, new short[128000]);
        var controlledHash = Hash(controlledSilence);
        for (int i = 0; i < 75; i++)
        {
            var split = await Feed(controlledSilence, controlledHash, "en");
            Require(split.GetProperty("segments").GetArrayLength() == 0 && split.GetProperty("vad_calls").GetInt32() == 0, "Controlled zero PCM suppresses VAD/ASR");
            controlledSilentAudio += 8; controlledSilentRounds++;
        }
        var silentState = await client.SendAsync("get_state");
        Require(silentState.GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetInt32() == 0
            && (await client.ReadHistoryAsync()).Records.Count == 0, "600 seconds controlled file PCM produces no ASR/history");
        File.WriteAllText(args[5], JsonSerializer.Serialize(new { passed = false, phase = "controlled_silence_complete",
            controlled_silence_audio_s = controlledSilentAudio, controlled_silence_rounds = controlledSilentRounds,
            controlled_silence_is_file_pcm = true, loopback_silence_gate_passed = false, state = silentState }));
        foreach (var fixture in fixtures)
        {
            var path = Path.GetFullPath(Path.Combine(fixtureRoot, fixture.GetProperty("path").GetString()!));
            var before = finals;
            var split = await Feed(path, fixture.GetProperty("sha256").GetString()!, fixture.GetProperty("language").GetString()!);
            var segments = split.GetProperty("segments").EnumerateArray().ToArray();
            if (fixture.GetProperty("kind").GetString() == "silence")
            {
                Require(segments.Length == 0 && split.GetProperty("vad_calls").GetInt32() == 0, "digital silence zero native calls");
                suppressed++;
            }
            else Require(segments.Length > 0 && finals > before, "synthetic speech has at least one source final");
            if (fixture.GetProperty("id").GetString() == "en-01")
                baselineRanges.AddRange(segments.Select(x => (x.GetProperty("audio_start_s").GetDouble(), x.GetProperty("audio_end_s").GetDouble())));
        }
        // Nonzero deterministic tone and low-level noise are diagnostic negatives.
        foreach (var kind in new[] { "tone", "noise" })
        {
            var path = Path.Combine(outputDirectory, kind + ".wav");
            WriteWav(path, kind);
            var split = await Feed(path, Hash(path), "en");
            Require(split.GetProperty("segments").GetArrayLength() == 0, "non-speech suppressed");
            Require(split.GetProperty("vad_calls").GetInt32() > 0, "nonzero input uses real VAD");
            suppressed++;
        }
        var first = fixtures[0];
        var firstPath = Path.GetFullPath(Path.Combine(fixtureRoot, first.GetProperty("path").GetString()!));
        for (int round = 0; round < 10; round++)
        {
            await client.SendAsync("reset_fixture_epoch");
            var split = await Feed(firstPath, first.GetProperty("sha256").GetString()!, "en");
            var ranges = split.GetProperty("segments").EnumerateArray().ToArray();
            Require(ranges.Length == baselineRanges.Count, "epoch reset reproduces segment count");
            var shift = ranges[0].GetProperty("audio_start_s").GetDouble() - baselineRanges[0].Item1;
            for (int i = 0; i < ranges.Length; i++)
                Require(Math.Abs(ranges[i].GetProperty("audio_end_s").GetDouble() - baselineRanges[i].Item2 - shift) < 1e-8, "recurrent reset reproduces ranges");
        }
        // Two utterances in one file get distinct segment IDs and bounded snapshots.
        var pairPath = Path.Combine(outputDirectory, "pair.wav");
        WritePair(firstPath, pairPath);
        var pair = await Feed(pairPath, Hash(pairPath), "en");
        Require(pair.GetProperty("segments").GetArrayLength() == 2, "two separated utterances");
        foreach (var badIndex in new[] { 11, 15 })
        {
            var badOptions = options.ToArray();
            badOptions[badIndex] = new string('0', 64);
            await using var badClient = WorkerClient.Start(args[0], arguments: badOptions);
            await badClient.SendAsync("hello", new { protocol_major = 1, client = "NativeVadHashSmoke" });
            var wait = Stopwatch.StartNew();
            while ((await badClient.SendAsync("get_state")).GetProperty("model").GetProperty("state").GetString() != "Ready")
            { Require(wait.Elapsed.TotalSeconds < 30, "negative probe ASR preparation"); await Task.Delay(5); }
            await badClient.SendAsync("transcribe_fixture", new { path = firstPath, sha256 = first.GetProperty("sha256").GetString(), language = "en" });
            bool rejected = false;
            while (!rejected && wait.Elapsed.TotalSeconds < 30)
            {
                while (badClient.Events.TryRead(out var message))
                    if (message!.Name == "fixture.failed")
                    { Require(message.Payload.GetProperty("code").GetString() == "VAD_MODEL_FAILED", "VAD hash rejection code"); rejected = true; }
                await badClient.SendAsync("ping", new { nonce = "after-vad-hash-error" });
                await Task.Delay(5);
            }
            Require(rejected, "VAD model/runtime hash rejected");
            Require((await badClient.SendAsync("get_state")).GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetInt32() == 0, "failed VAD never calls ASR");
        }
        var state = await client.SendAsync("get_state");
        Require(state.GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetInt32() == finals + failed + skipped, "suppressed inputs never called ASR");
        Require(decodeDiagnostics.Any(d => d.GetProperty("timed_token_count").GetInt32() > 0), "Real token timestamps extracted");
        var report = new { passed = true, quality_gate_passed = false, backend = args[4], vad_model = "silero-v6.0", runtime = "1.22.0", finals, failed,
            suppressed, vad_calls = calls, epoch_reset_rounds = 10, pair_segments = 2, model_hash_rejected = true, runtime_hash_rejected = true,
            controlled_silence_audio_s = controlledSilentAudio, controlled_silence_rounds = controlledSilentRounds,
            controlled_silence_is_file_pcm = true, loopback_silence_gate_passed = false,
            skipped, decode_diagnostics = decodeDiagnostics,
            vad_mean_s = vadSeconds.Average(), vad_max_s = vadSeconds.Max(), ping_max_s = pingMax,
            capture = false, partial = false, translation = false, fixtures = "synthetic_tts_and_generated_negatives" };
        File.WriteAllText(args[5], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));

        async Task<JsonElement> Feed(string path, string hash, string language)
        {
            var accepted = await client.SendAsync("transcribe_fixture", new { path, sha256 = hash, language });
            var split = await Event("fixture.segmented", accepted.GetProperty("fixture_id").GetUInt64());
            Console.WriteLine($"VAD fixture {Path.GetFileName(path)}: {split.GetProperty("segments").GetArrayLength()} segment(s)");
            vadSeconds.Add(split.GetProperty("vad_s").GetDouble());
            calls += split.GetProperty("vad_calls").GetInt32();
            foreach (var segment in split.GetProperty("segments").EnumerateArray())
            {
                Require(segment.GetProperty("queued").GetBoolean(), "fixture final admitted");
                var final = await Event("source.terminal", segment.GetProperty("segment_id").GetUInt64());
                var record = final.GetProperty("record");
                if (record.GetProperty("source_state").GetString() == "Skipped")
                {
                    Require(record.GetProperty("source_reason").GetString() is "NoSpeech" or "OverlapOnly"
                        && record.GetProperty("translation_state").GetString() == "None", "Explicit no-speech skip");
                    skipped++; continue;
                }
                if (record.GetProperty("source_state").GetString() == "Failed")
                {
                    Require(record.GetProperty("source_reason").GetString() == "InvalidText" && record.GetProperty("source").GetString() == "", "empty output remains failed");
                    failed++;
                    continue;
                }
                Require(!string.IsNullOrWhiteSpace(record.GetProperty("source").GetString()), "real source final");
                Require(record.GetProperty("translation_state").GetString() == "None", "ASR only");
                Require(record.GetProperty("audio_end_s").GetDouble() - record.GetProperty("audio_start_s").GetDouble() <= 8, "bounded range");
                finals++;
            }
            await client.ReadHistoryAsync();
            return split;
        }
        async Task<JsonElement> Event(string name, ulong? identity = null)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 30)
            {
                while (client.Events.TryRead(out var e))
                {
                    if (e!.Name == "fixture.failed") throw new Exception("Fixture failed: " + e.Payload.GetProperty("code").GetString());
                    if (e.Name == "asr.completed") decodeDiagnostics.Add(e.Payload.Clone());
                    if (e.Name == "segment.skipped" && e.Payload.GetProperty("record").GetProperty("source_reason").GetString() is not ("NoSpeech" or "OverlapOnly"))
                        throw new Exception("Unexpected final admission skip");
                    if (e.Name != name && !(name == "source.terminal" && e.Name is "source.final" or "segment.failed" or "segment.skipped")) continue;
                    var payload = e.Payload;
                    var key = payload.TryGetProperty("record", out var record) ? record.GetProperty("segment_id").GetUInt64() :
                        payload.TryGetProperty("fixture_id", out var id) ? id.GetUInt64() : 0;
                    if (!identity.HasValue || key == identity) return payload;
                }
                var ping = Stopwatch.StartNew();
                await client.SendAsync("ping", new { nonce = "during-vad" });
                pingMax = Math.Max(pingMax, ping.Elapsed.TotalSeconds);
                await Task.Delay(5);
            }
            throw new TimeoutException(name);
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Require(bool valid, string label) { if (!valid) throw new Exception("Native VAD smoke failed: " + label); }
    private static void WriteWav(string path, string kind)
    {
        var samples = new short[32000];
        var random = new Random(12345);
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(kind == "tone" ? 1600 * Math.Sin(2 * Math.PI * 1000 * i / 16000) : 100 * (random.NextDouble() * 2 - 1));
        Save(path, samples);
    }
    private static void WritePair(string input, string path)
    {
        // Locate the PCM data chunk rather than assuming a 44-byte TTS header.
        using var reader = new BinaryReader(File.OpenRead(input));
        reader.ReadBytes(12);
        short[] samples = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var name = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            var bytes = reader.ReadBytes(size);
            if ((size & 1) != 0) reader.ReadByte();
            if (name == "data") { samples = new short[size / 2]; Buffer.BlockCopy(bytes, 0, samples, 0, size); break; }
        }
        Require(samples.Length > 0, "pair PCM found");
        Save(path, samples.Concat(new short[11200]).Concat(samples).ToArray());
    }
    private static void Save(string path, short[] samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVEfmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2); foreach (var sample in samples) w.Write(sample);
    }
}
