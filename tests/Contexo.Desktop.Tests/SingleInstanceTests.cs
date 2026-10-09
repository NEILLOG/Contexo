using Avalonia.Styling;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Platform.Common;

namespace Contexo.Desktop.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task Second_instance_is_not_primary_and_wakes_the_first()
    {
        var name = "Contexo.Test." + Guid.NewGuid().ToString("N")[..12];
        using var first = SingleInstance.Acquire(name);
        var activated = new TaskCompletionSource();
        first.ActivationRequested += (_, _) => activated.TrySetResult();
        first.StartListening();

        using var second = SingleInstance.Acquire(name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        Assert.True(await second.NotifyPrimaryAsync(TimeSpan.FromSeconds(5)));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Can_be_woken_more_than_once()
    {
        var name = "Contexo.Test." + Guid.NewGuid().ToString("N")[..12];
        using var first = SingleInstance.Acquire(name);
        var count = 0;
        var twice = new TaskCompletionSource();
        first.ActivationRequested += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                twice.TrySetResult();
            }
        };
        first.StartListening();
        using var second = SingleInstance.Acquire(name);

        Assert.True(await second.NotifyPrimaryAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.NotifyPrimaryAsync(TimeSpan.FromSeconds(5)));

        await twice.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Notifying_without_a_running_instance_fails_quickly()
    {
        using var lonely = SingleInstance.Acquire("Contexo.Test." + Guid.NewGuid().ToString("N")[..12]);

        Assert.False(await lonely.NotifyPrimaryAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void Default_name_contains_the_user_name()
    {
        Assert.Contains(Environment.UserName, SingleInstance.DefaultName);
    }

    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public void Theme_preference_maps_to_a_theme_variant(ThemePreference preference)
    {
        var expected = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        Assert.Equal(expected, ThemeManager.ToVariant(preference));
    }
}
