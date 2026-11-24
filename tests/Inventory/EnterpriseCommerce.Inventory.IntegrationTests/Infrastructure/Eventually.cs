namespace EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

/// <summary>
/// Waits for a condition that a background component makes true, with a deadline: a bounded poll, never a fixed sleep.
/// It fails with the given description when the deadline passes.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    public static async Task TrueAsync(Func<Task<bool>> condition, TimeSpan deadline, string? description = null)
    {
        var expires = DateTimeOffset.UtcNow + deadline;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= expires)
            {
                Assert.Fail($"Not reached within {deadline}: {description ?? "the condition"}.");
            }

            await Task.Delay(Interval, TestContext.Current.CancellationToken);
        }
    }
}
