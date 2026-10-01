using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Text.Json;

namespace EchoSub.Desktop;

public sealed record HistoryRecord(
    [property: JsonPropertyName("session_id")] ulong SessionId,
    [property: JsonPropertyName("epoch")] ulong Epoch,
    [property: JsonPropertyName("segment_id")] ulong SegmentId,
    [property: JsonPropertyName("source_revision")] ulong SourceRevision,
    [property: JsonPropertyName("applied_source_revision")] ulong? AppliedSourceRevision,
    [property: JsonPropertyName("audio_start_sample")] ulong AudioStartSample,
    [property: JsonPropertyName("audio_end_sample")] ulong AudioEndSample,
    [property: JsonPropertyName("audio_start_s")] double AudioStartSeconds,
    [property: JsonPropertyName("audio_end_s")] double AudioEndSeconds,
    [property: JsonPropertyName("source_state")] string SourceState,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("source_reason")] string? SourceReason,
    [property: JsonPropertyName("translation_state")] string TranslationState,
    [property: JsonPropertyName("translation")] string Translation,
    [property: JsonPropertyName("translation_reason")] string? TranslationReason,
    [property: JsonPropertyName("translation_request_id")] ulong? TranslationRequestId,
    [property: JsonPropertyName("product_session_id")] string? ProductSessionId = null,
    [property: JsonPropertyName("session_audio_start_s")] double? SessionAudioStartSeconds = null,
    [property: JsonPropertyName("session_audio_end_s")] double? SessionAudioEndSeconds = null,
    [property: JsonPropertyName("session_started_at_utc")] string? SessionStartedAtUtc = null,
    [property: JsonPropertyName("stable_source")] string StableSource = "",
    [property: JsonPropertyName("translation_is_preview")] bool TranslationIsPreview = false);
public sealed record HistorySnapshot(ulong Version, ulong LastSequence, IReadOnlyList<HistoryRecord> Records);
public sealed record WorkerEvent(ulong Sequence, string Name, JsonElement Payload);

// Reader never invokes UI handlers or waits for UI consumption.
public sealed class WorkerEventBuffer
{
    private readonly object gate = new();
    private readonly Channel<byte> changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });
    private readonly Channel<WorkerEvent> channel = Channel.CreateBounded<WorkerEvent>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });
    private ulong lastSequence;
    private ulong problemSequence;
    private bool needsSnapshot;
    public bool SnapshotRequired { get { lock (gate) return needsSnapshot; } }
    public ulong LastSequence { get { lock (gate) return lastSequence; } }
    public ValueTask<byte> WaitForChangeAsync(CancellationToken cancellationToken) => changes.Reader.ReadAsync(cancellationToken);
    public void Publish(WorkerEvent message)
    {
        lock (gate)
        {
            if (message.Sequence == 0) throw new IOException("Invalid worker event sequence");
            if (message.Sequence <= lastSequence) return; // snapshot covers delayed events
            if (message.Sequence != lastSequence + 1 || message.Name is "snapshot.required" or "history.changed")
            {
                needsSnapshot = true;
                problemSequence = message.Sequence;
            }
            lastSequence = message.Sequence;
            if (!channel.Writer.TryWrite(message))
            {
                needsSnapshot = true;
                problemSequence = message.Sequence;
            }
            // One coalesced wakeup; the protocol reader never calls UI code.
            changes.Writer.TryWrite(0);
        }
    }
    public bool TryRead(out WorkerEvent? message)
    {
        lock (gate) return channel.Reader.TryRead(out message);
    }
    public void AcceptSnapshot(ulong sequence)
    {
        lock (gate)
        {
            while (channel.Reader.TryPeek(out var message) && message.Sequence <= sequence) channel.Reader.TryRead(out _);
            lastSequence = Math.Max(lastSequence, sequence);
            if (problemSequence <= sequence) needsSnapshot = false;
        }
    }
}
