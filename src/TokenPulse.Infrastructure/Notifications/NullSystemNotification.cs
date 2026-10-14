using TokenPulse.Core.Abstractions;

namespace TokenPulse.Infrastructure.Notifications;

public class NullSystemNotification : ISystemNotification
{
    public Task ShowAsync(string title, string message, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
