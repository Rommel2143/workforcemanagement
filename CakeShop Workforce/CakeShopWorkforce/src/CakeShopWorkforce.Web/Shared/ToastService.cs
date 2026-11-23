namespace CakeShopWorkforce.Web.Shared;

/// <summary>
/// Shows small pop-up messages ("Shift saved") in the corner of the screen.
///
/// Usage in any page:
/// <code>
///   @inject ToastService Toasts
///   Toasts.Success("Employee saved");
/// </code>
/// The ToastHost component (in MainLayout) listens and draws them.
/// </summary>
public sealed class ToastService
{
    public event Action? Changed;

    public List<Toast> Items { get; } = [];

    public void Success(string message) => Show(message, "success");
    public void Error(string message) => Show(message, "danger");
    public void Warning(string message) => Show(message, "warning");
    public void Info(string message) => Show(message, "info");

    private void Show(string message, string tone)
    {
        var toast = new Toast(Guid.NewGuid(), message, tone);
        Items.Add(toast);
        Changed?.Invoke();

        // Remove automatically after 4 seconds
        _ = Task.Delay(4000).ContinueWith(_ => Dismiss(toast.Id));
    }

    public void Dismiss(Guid id)
    {
        Items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }
}

public sealed record Toast(Guid Id, string Message, string Tone);
