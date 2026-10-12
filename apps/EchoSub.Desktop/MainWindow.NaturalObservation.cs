using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    private readonly bool naturalObservation;
    private string? observedCaptureState;
    private string? observedCaptureError;
    private double? observedAcceptedAudioSeconds;
    internal (string? State, bool Ready, bool Busy, bool Closed) NaturalState =>
        (sessionState, startCaptureButton.IsEnabled, pendingActions != 0 || translationBusy, closeReady);
    internal string? NaturalSessionId => sessionId;
    internal bool NaturalCaptureRunning => observedCaptureState == "Running";
    internal double? NaturalAcceptedAudioSeconds => observedAcceptedAudioSeconds;
    internal int NaturalTranslatedCount => snapshot?.Records.Count(r => r.ProductSessionId == sessionId &&
        r.TranslationState == "Done" && r.AppliedSourceRevision == r.SourceRevision) ?? 0;

    internal async Task NaturalConfigureAsync()
    {
        RequireNaturalObservation();
        language.SelectedItem = "en";
        overlayWidth.Value = 760; cardOpacity.Value = 0.75;
        overlaySourceEnabled.IsChecked = false;
        partialEnabled.IsChecked = partialTranslationEnabled.IsChecked = cosmeticRevisions.IsChecked = true;
        translationProfile.SelectedItem = profileChoices.Single(choice => choice.Id == "hymt2-greedy");
        await ConfigureTranslationAsync(false);
    }

    internal void NaturalClick(string action)
    {
        RequireNaturalObservation();
        var button = action switch { "start" => startCaptureButton, "stop" => stopCaptureButton,
            "overlay" => overlayButton, _ => throw new ArgumentException("Unknown observation action") };
        if (!button.IsEnabled) throw new IOException("Observation action unavailable: " + action + " · " + status.Text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal object NaturalSnapshot()
    {
        RequireNaturalObservation();
        var records = snapshot?.Records.Where(r => r.ProductSessionId == sessionId).ToArray() ?? [];
        long? workerBytes = null;
        if (client is not null)
        {
            try { using var worker = System.Diagnostics.Process.GetProcessById(client.ProcessId); workerBytes = worker.PrivateMemorySize64; }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        using var desktop = System.Diagnostics.Process.GetCurrentProcess();
        return new { product_session_id = sessionId, session_state = sessionState, capture_state = observedCaptureState, worker_pid = client?.ProcessId,
            capture_error = observedCaptureError, accepted_audio_s = observedAcceptedAudioSeconds,
            translator_ready = translationConfigured, busy = translationBusy, profile = appliedProfile,
            retained_records = records.Length, final_records = records.Count(r => r.SourceState == "Final"),
            translated_records = records.Count(r => r.TranslationState == "Done" && r.AppliedSourceRevision == r.SourceRevision),
            failed_sources = records.Count(r => r.SourceState == "Failed"),
            failed_translations = records.Count(r => r.TranslationState == "Failed"),
            desktop_private_bytes = desktop.PrivateMemorySize64, worker_private_bytes = workerBytes };
    }

    private void RequireNaturalObservation()
    {
        if (!naturalObservation || !live || sessionProbeWorker is not null)
            throw new InvalidOperationException("Explicit native live observation required");
    }
}
