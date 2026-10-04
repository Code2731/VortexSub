using Avalonia.Interactivity;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    internal DesktopPreferences RecoveryPreferences => new()
    {
        Language = language.SelectedItem as string ?? "en", DeviceId = (endpoint.SelectedItem as EndpointChoice)?.Id,
        OverlayWidth = overlayWidth.Value, CardOpacity = cardOpacity.Value,
        ShowSource = overlaySourceEnabled.IsChecked == true, Partial = partialEnabled.IsChecked == true,
        PartialTranslation = partialTranslationEnabled.IsChecked == true, CosmeticRevisions = cosmeticRevisions.IsChecked == true
    };
    internal string RecoveryPreferencesStatus => preferencesStatus.Text ?? "";
    internal string RecoveryTranslationStatus => translationStatus.Text ?? "";
    internal string RecoveryTranslationError => translationErrorDetails.Text ?? "";
    internal bool RecoveryTranslationReady => translationConfigured && !translationBusy;

    internal void RecoverySetPreferences(double width = 900)
    {
        RequireSessionProbe();
        language.SelectedItem = "ja";
        overlayWidth.Value = width; cardOpacity.Value = 0.5;
        overlaySourceEnabled.IsChecked = true;
        partialTranslationEnabled.IsChecked = true;
        cosmeticRevisions.IsChecked = true;
    }
    internal void RecoverySave()
    {
        RequireSessionProbe();
        savePreferencesButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }
    internal void RecoveryDevices(params EndpointChoice[] choices) { RequireSessionProbe(); RestoreDeviceChoices(choices); }
    internal async Task RecoveryConfigure(string address, string profile = "standard")
    {
        RequireSessionProbe();
        translationEndpoint.Text = address;
        translationProfile.SelectedItem = profileChoices.Single(choice => choice.Id == profile);
        await ConfigureTranslationAsync(false);
    }
    internal Task RecoveryPoll() { RequireSessionProbe(); return PollAsync(); }
    internal static bool RecoveryEndpointValid(string value) => IsLocalTranslationEndpoint(value);
}
