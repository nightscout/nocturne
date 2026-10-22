using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Nocturne.API.Tests.GoldenFiles.Infrastructure;

/// <summary>
/// A golden file factory whose clock stands just after the fixed 2024-03-26 readings the v1
/// entries goldens seed, so they sit inside the 4-day default read window.
/// </summary>
public class FixedClockGoldenFileWebAppFactory : GoldenFileWebAppFactory
{
    public static readonly DateTimeOffset Now = new(2024, 3, 26, 13, 0, 0, TimeSpan.Zero);

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
    }
}
