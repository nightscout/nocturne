using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Validators.V4;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// The validators as <c>Program</c> registers them: scanned off the API assembly, so a request
/// type nobody wrote a validator for resolves to nothing here too. MVC comes with them because
/// a request-services-bearing controller resolves its <see cref="ProblemDetailsFactory"/> from
/// there rather than falling back to the built-in one.
/// </summary>
internal static class RegisteredValidators
{
    public static IServiceProvider Provider { get; } = With(_ => { });

    /// <summary>The validators <c>Program</c> registers, and the ones a test adds for a request type of its own.</summary>
    public static ServiceProvider With(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        services.AddValidatorsFromAssemblyContaining<UpsertSensorGlucoseRequestValidator>();
        register(services);
        return services.BuildServiceProvider();
    }

    public static TController On<TController>(TController controller)
        where TController : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = Provider },
        };
        return controller;
    }
}
