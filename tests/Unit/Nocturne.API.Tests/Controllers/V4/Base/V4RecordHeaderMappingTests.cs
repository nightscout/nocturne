using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// Every V4 record type takes the header of its request (time, offset, device, app, data source)
/// onto the model on create and on update, and never keeps the stored one.
/// </summary>
[Trait("Category", "Unit")]
public class V4RecordHeaderMappingTests
{
    private static readonly DateTimeOffset Sent = new(2026, 7, 1, 12, 30, 0, TimeSpan.FromHours(10));
    private static readonly DateTime Stored = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<Type> CrudControllerTypes() =>
        typeof(V4RecordHeaderMapping).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false } && CrudBaseOf(t) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

    public static TheoryData<Type> CrudControllers()
    {
        var data = new TheoryData<Type>();
        foreach (var type in CrudControllerTypes())
            data.Add(type);

        return data;
    }

    [Theory]
    [MemberData(nameof(CrudControllers))]
    public void Create_TakesTheRequestsHeader(Type controllerType)
    {
        var arguments = CrudBaseOf(controllerType)!.GetGenericArguments();
        var request = Request(arguments[1]);

        var model = (IV4Record)Invoke(controllerType, "MapCreateToModel", request);

        AssertHeader(model, expectedTimestamp: Sent.UtcDateTime, expectedSuffix: "sent");
    }

    [Theory]
    [MemberData(nameof(CrudControllers))]
    public void Update_TakesTheRequestsHeaderAndNotTheStoredOne(Type controllerType)
    {
        var arguments = CrudBaseOf(controllerType)!.GetGenericArguments();
        var request = Request(arguments[2]);
        var existing = (IV4Record)Activator.CreateInstance(arguments[0])!;
        existing.Timestamp = Stored;
        existing.UtcOffset = -1;
        existing.Device = "stored";
        existing.App = "stored";
        existing.DataSource = "stored";

        var model = (IV4Record)Invoke(controllerType, "MapUpdateToModel", Guid.CreateVersion7(), request, existing);

        AssertHeader(model, expectedTimestamp: Sent.UtcDateTime, expectedSuffix: "sent");
    }

    [Theory]
    [InlineData(typeof(ApsSnapshotController), typeof(UpsertApsSnapshotRequest))]
    [InlineData(typeof(PumpSnapshotController), typeof(UpsertPumpSnapshotRequest))]
    [InlineData(typeof(UploaderSnapshotController), typeof(UpsertUploaderSnapshotRequest))]
    public void Snapshot_TakesTheRequestsHeader(Type controllerType, Type requestType)
    {
        var method = controllerType.GetMethod("MapToModel", BindingFlags.Static | BindingFlags.NonPublic)!;

        var model = (IV4Record)method.Invoke(null, [Request(requestType)])!;

        AssertHeader(model, expectedTimestamp: Sent.UtcDateTime, expectedSuffix: "sent");
    }

    [Fact]
    public void EveryRequestWithARecordHeader_IsMappedByATestInThisClass()
    {
        var covered = CrudControllerTypes().SelectMany(type => CrudBaseOf(type)!.GetGenericArguments().Skip(1).Take(2))
            .Concat([typeof(UpsertApsSnapshotRequest), typeof(UpsertPumpSnapshotRequest), typeof(UpsertUploaderSnapshotRequest)])
            .Concat([typeof(CreateMealRequest)])
            .ToHashSet();

        var all = typeof(IV4RecordHeader).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IV4RecordHeader).IsAssignableFrom(t));

        all.Should().OnlyContain(t => covered.Contains(t), "a request type with a record header ships with a mapping test");
    }

    [Fact]
    public void WithHeaderFrom_SetsTheSpanStartOfATempBasal()
    {
        var model = new TempBasal().WithHeaderFrom((IV4RecordHeader)Request(typeof(CreateTempBasalRequest)));

        model.StartTimestamp.Should().Be(Sent.UtcDateTime);
        model.StartTimestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static void AssertHeader(IV4Record model, DateTime expectedTimestamp, string expectedSuffix)
    {
        model.Timestamp.Should().Be(expectedTimestamp);
        model.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        model.UtcOffset.Should().Be(600);
        model.Device.Should().Be($"device-{expectedSuffix}");
        model.App.Should().Be($"app-{expectedSuffix}");
        model.DataSource.Should().Be($"source-{expectedSuffix}");
    }

    private static object Request(Type requestType)
    {
        requestType.Should().BeAssignableTo<IV4RecordHeader>();
        var request = Activator.CreateInstance(requestType)!;
        Set(request, "Timestamp", Sent);
        Set(request, "UtcOffset", 600);
        Set(request, "Device", "device-sent");
        Set(request, "App", "app-sent");
        Set(request, "DataSource", "source-sent");
        return request;
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);

    private static object Invoke(Type controllerType, string method, params object[] arguments)
    {
        var controller = (ControllerBase)RuntimeHelpers.GetUninitializedObject(controllerType);
        var target = controllerType.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{controllerType.Name} declares no {method}");

        return target.Invoke(controller, arguments)!;
    }

    private static Type? CrudBaseOf(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(V4CrudControllerBase<,,,>))
                return current;
        }

        return null;
    }
}
