using Contexo.Core.Abstractions;

namespace Contexo.Core.Indexing;

/// <summary>Default <see cref="IUserActivityMonitor"/>: the user is always idle. Contexo.Desktop replaces it per platform.</summary>
internal sealed class AlwaysIdleActivityMonitor : IUserActivityMonitor
{
    public TimeSpan IdleTime => TimeSpan.MaxValue;
}
