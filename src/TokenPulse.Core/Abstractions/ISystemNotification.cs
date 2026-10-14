namespace TokenPulse.Core.Abstractions;

public interface ISystemNotification
{
    Task ShowAsync(string title, string message, CancellationToken ct = default);
}
