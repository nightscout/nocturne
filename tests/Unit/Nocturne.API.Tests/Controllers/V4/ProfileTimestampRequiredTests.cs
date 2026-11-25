using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The profile schedule writes check the timestamp before they touch a dependency, and answer a
/// missing one with <see cref="TimestampRule.Message"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileTimestampRequiredTests
{
    public static TheoryData<string> Writes()
    {
        var writes = typeof(ProfileController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a is HttpPostAttribute or HttpPutAttribute))
            .Where(m => m.GetParameters().Any(p => p.GetCustomAttribute<FromBodyAttribute>() is not null
                && typeof(IV4Record).IsAssignableFrom(p.ParameterType)))
            .Select(m => m.Name)
            .Order()
            .ToList();

        writes.Should().NotBeEmpty("the profile controller writes V4 records");
        var data = new TheoryData<string>();
        foreach (var write in writes)
            data.Add(write);
        return data;
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_WithoutATimestamp_IsRefusedWithTheOneMessage(string action)
    {
        var controller = (ProfileController)RuntimeHelpers.GetUninitializedObject(typeof(ProfileController));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var method = typeof(ProfileController).GetMethod(action)!;
        var parameters = method.GetParameters();
        var arguments = parameters
            .Select<ParameterInfo, object?>(p => p.ParameterType == typeof(Guid) ? Guid.NewGuid()
                : p.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                : Activator.CreateInstance(p.ParameterType))
            .ToArray();

        var task = (Task)method.Invoke(controller, arguments)!;
        await task;

        var actionResult = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var result = actionResult.GetType().GetProperty("Result")!.GetValue(actionResult)
            .Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(400);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(TimestampRule.Message);
    }
}
