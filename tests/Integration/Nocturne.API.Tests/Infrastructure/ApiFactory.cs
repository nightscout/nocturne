using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the API's real top-level
/// program.
/// </summary>
/// <remarks>
/// The API's entry-point type also carries the static <c>CreateHostBuilder</c> NSwag discovers,
/// and WebApplicationFactory looks for that convention on the assembly's entry point before it
/// falls back to the minimal-hosting program. Left alone it stands up NSwag's schema-only host,
/// which has no endpoints and answers every request 404. Returning null here forces the real
/// program, as <c>SqliteWebAppFactoryBase</c> does for the unit suites.
/// </remarks>
public class ApiFactory : WebApplicationFactory<Nocturne.API.Program>
{
    protected override IHostBuilder? CreateHostBuilder() => null;
}
