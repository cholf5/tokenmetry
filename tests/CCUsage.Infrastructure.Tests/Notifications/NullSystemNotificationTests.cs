namespace CCUsage.Infrastructure.Tests.Notifications;

using CCUsage.Infrastructure.Notifications;

public class NullSystemNotificationTests
{
    [Fact]
    public async Task Show_notification_completes_without_error()
    {
        var notification = new NullSystemNotification();
        var action = () => notification.ShowAsync("Test Title", "Test Message");
        await action.Should().NotThrowAsync();
    }
}
