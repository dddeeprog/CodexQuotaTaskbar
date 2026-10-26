using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private KeyboardInteractionController? typingInteraction;

    private void StartTypingInteraction(bool demo, AppSettings settings)
    {
        if (coordinator is null) return;
        // Demo/preview startup never observes the user's real keyboard.
        if (!demo)
        {
            typingInteraction = new KeyboardInteractionController(() => new KeyboardActivitySource(),
                value => { if (!shuttingDown) coordinator?.KeyboardActivity(value); });
            coordinator.TypingAvailabilityChanged += OnTypingAvailabilityChanged;
        }
        ApplyTypingInteraction(settings);
    }

    private void ApplyTypingInteraction(AppSettings settings)
    {
        coordinator?.SetCompanionTyping(settings.CompanionTyping);
        typingInteraction?.Update(settings.CompanionTyping, coordinator?.CanAnimateTyping == true);
    }

    private void OnTypingAvailabilityChanged(bool available) =>
        typingInteraction?.Update(!shuttingDown && tray?.Settings.CompanionTyping == true, available);

    private void StopTypingInteraction()
    {
        if (coordinator is not null) coordinator.TypingAvailabilityChanged -= OnTypingAvailabilityChanged;
        typingInteraction?.Dispose();
        typingInteraction = null;
    }
}
