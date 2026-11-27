using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Base;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// Every V4 action whose body is an array or list of a validated type refuses an invalid item with a 400 before it
/// reaches a dependency. The controller is built uninitialised, so an action that touches a service before it runs
/// <see cref="V4BulkValidation.ValidateEachAsync{TRequest}"/> fails here with a null reference.
/// </summary>
[Trait("Category", "Unit")]
public class RootArrayBodyValidationTests
{
    [Theory]
    [MemberData(nameof(RootArrayActions))]
    public async Task An_invalid_item_is_refused_before_anything_is_stored(Type controllerType, string actionName)
    {
        var action = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == actionName && BodyOf(m) is not null);
        var body = BodyOf(action)!;
        var elementType = ElementTypeOf(body.ParameterType)!;

        var items = Array.CreateInstance(elementType, 1);
        items.SetValue(InvalidItem(elementType), 0);

        var arguments = action.GetParameters()
            .Select(p => p == body ? BodyValue(p.ParameterType, items) : Default(p))
            .ToArray();

        var controller = RegisteredValidators.On((ControllerBase)RuntimeHelpers.GetUninitializedObject(controllerType));
        var result = await ResultOf(action.Invoke(controller, arguments));

        result.Should().BeAssignableTo<IStatusCodeActionResult>()
            .Which.StatusCode.Should().Be(400, $"{controllerType.Name}.{actionName} validates each item of its body");
    }

    public static TheoryData<Type, string> RootArrayActions()
    {
        var data = new TheoryData<Type, string>();
        foreach (var (controller, action) in typeof(V4BulkValidation).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                                 && typeof(ControllerBase).IsAssignableFrom(t)
                                 && t.Namespace?.StartsWith("Nocturne.API.Controllers.V4", StringComparison.Ordinal) == true)
                     .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => BodyOf(m) is { } body && ElementTypeOf(body.ParameterType) is { } element && IsValidated(element))
                         .Select(m => (c, m.Name)))
                     .Distinct()
                     .OrderBy(a => a.c.Name, StringComparer.Ordinal)
                     .ThenBy(a => a.Name, StringComparer.Ordinal))
        {
            data.Add(controller, action);
        }

        return data;
    }

    private static ParameterInfo? BodyOf(MethodInfo action) =>
        action.GetParameters().SingleOrDefault(p => p.IsDefined(typeof(FromBodyAttribute), inherit: true));

    private static Type? ElementTypeOf(Type body)
    {
        if (body.IsArray)
        {
            return body.GetElementType();
        }

        return body != typeof(string)
            ? body.GetInterfaces().Append(body)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0]
            : null;
    }

    private static bool IsValidated(Type type) => RegisteredValidators.Provider.GetService(ValidatorTypeOf(type)) is not null;

    private static Type ValidatorTypeOf(Type type) => typeof(IValidator<>).MakeGenericType(type);

    /// <summary>
    /// A fresh item, or one whose strings are made longer than any column until its validator refuses it.
    /// </summary>
    private static object InvalidItem(Type elementType)
    {
        var validator = (IValidator)RegisteredValidators.Provider.GetRequiredService(ValidatorTypeOf(elementType));
        var item = elementType.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(elementType)!
            : RuntimeHelpers.GetUninitializedObject(elementType);

        foreach (var property in elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        {
            if (!validator.Validate(new ValidationContext<object>(item)).IsValid)
            {
                break;
            }

            property.SetValue(item, new string('a', 100_000));
        }

        validator.Validate(new ValidationContext<object>(item)).IsValid.Should().BeFalse($"an invalid {elementType.Name} can be built");
        return item;
    }

    private static object BodyValue(Type parameterType, Array items) =>
        parameterType.IsInstanceOfType(items)
            ? items
            : Activator.CreateInstance(parameterType, items)!;

    private static object? Default(ParameterInfo parameter) =>
        parameter.HasDefaultValue ? parameter.DefaultValue
        : parameter.ParameterType == typeof(CancellationToken) ? CancellationToken.None
        : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
        : null;

    private static async Task<IActionResult> ResultOf(object? returned)
    {
        if (returned is Task task)
        {
            await task;
            returned = task.GetType().GetProperty("Result")?.GetValue(task);
        }

        return returned switch
        {
            IConvertToActionResult convertible => convertible.Convert(),
            IActionResult result => result,
            _ => throw new InvalidOperationException($"the action returned {returned?.GetType().Name ?? "nothing"}"),
        };
    }
}
