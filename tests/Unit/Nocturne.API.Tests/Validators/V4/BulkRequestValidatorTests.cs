using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentValidation;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Tests.Controllers.V4.Base;
using Nocturne.API.Validators.V4;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

/// <summary>
/// <see cref="V4BulkValidation.ValidateBulkAsync{TRequest}"/> holds no rule of its own, so every bulk request type
/// resolves a validator that refuses an unset timestamp and, when the type carries a sync key, a sync key with no
/// data source.
/// </summary>
[Trait("Category", "Unit")]
public class BulkRequestValidatorTests
{
    public static TheoryData<Type> BulkRequests()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(IBulkUpsertRequest).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IBulkUpsertRequest).IsAssignableFrom(t))
                     .OrderBy(t => t.Name))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BulkRequests))]
    public void A_bulk_request_with_an_unset_timestamp_is_refused_by_its_validator(Type requestType)
    {
        var request = New(requestType);

        Failures(requestType, request).Should().Contain(f => f.PropertyName == nameof(IBulkUpsertRequest.Timestamp));
    }

    [Theory]
    [MemberData(nameof(BulkRequests))]
    public void A_bulk_request_with_a_sync_key_and_no_data_source_is_refused_by_its_validator(Type requestType)
    {
        if (requestType.GetProperty(nameof(IBulkUpsertRequest.SyncIdentifier)) is not { } syncIdentifier)
        {
            return;
        }

        var request = New(requestType);
        requestType.GetProperty(nameof(IBulkUpsertRequest.Timestamp))!.SetValue(request, DateTimeOffset.UtcNow);
        syncIdentifier.SetValue(request, "abc");

        Failures(requestType, request).Should().Contain(f => f.PropertyName == nameof(IBulkUpsertRequest.DataSource)
                                                             && f.ErrorMessage == RecordColumnRules.DataSourceRequiredMessage);
    }

    private static object New(Type requestType) =>
        requestType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null
            ? Activator.CreateInstance(requestType)!
            : RuntimeHelpers.GetUninitializedObject(requestType);

    private static IList<FluentValidation.Results.ValidationFailure> Failures(Type requestType, object request)
    {
        var validator = RegisteredValidators.Provider.GetService(typeof(IValidator<>).MakeGenericType(requestType)) as IValidator;
        validator.Should().NotBeNull($"{requestType.Name} is a bulk request, and the bulk path runs only its validator");

        var context = (IValidationContext)Activator.CreateInstance(typeof(ValidationContext<>).MakeGenericType(requestType), request)!;
        return validator!.Validate(context).Errors;
    }
}
