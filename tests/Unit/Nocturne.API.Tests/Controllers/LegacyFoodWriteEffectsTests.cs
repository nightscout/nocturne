using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using FluentAssertions;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;
using LegacyV1 = Nocturne.API.Controllers.V1;
using LegacyV3 = Nocturne.API.Controllers.V3;

namespace Nocturne.API.Tests.Controllers;

public class LegacyFoodWriteEffectsTests
{
    private readonly Mock<IFoodRepository> _repository = new();
    private readonly Mock<IWriteSideEffects> _sideEffects = new();
    private readonly Mock<IDataEventSink<Food>> _events = new();
    private Food[] _written = [];
    private readonly Food _stored = new() { Id = "stored", Name = "Apple" };

    public LegacyFoodWriteEffectsTests()
    {
        _repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Food>, CancellationToken>((foods, _) => _written = foods.ToArray())
            .ReturnsAsync((IEnumerable<Food> foods, CancellationToken _) =>
                foods.Select(f => { f.Id = "stored"; return f; }).ToList());
        _repository
            .Setup(r => r.UpdateFoodAsync("stored", It.IsAny<Food>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Food f, CancellationToken _) => f);
        _repository
            .Setup(r => r.GetFoodByIdAsync("stored", It.IsAny<CancellationToken>()))
            .ReturnsAsync(_stored);
        _repository
            .Setup(r => r.DeleteFoodAsync("stored", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repository
            .Setup(r => r.BulkDeleteFoodAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
    }

    private LegacyV1.FoodController V1Controller(string queryString = "") =>
        WithContext(
            new LegacyV1.FoodController(
                _repository.Object,
                FoodServices.Over(_repository.Object, _sideEffects.Object, _events.Object),
                NullLogger<LegacyV1.FoodController>.Instance),
            queryString);

    private LegacyV3.FoodController V3Controller() =>
        WithContext(
            new LegacyV3.FoodController(
                _repository.Object,
                FoodServices.Over(_repository.Object, _sideEffects.Object, _events.Object),
                Mock.Of<IDocumentProcessingService>(),
                NullLogger<LegacyV3.FoodController>.Instance),
            "");

    private static T WithContext<T>(T controller, string queryString) where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(queryString);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private void VerifyBoth(
        Expression<Func<IWriteSideEffects, Task>> effect,
        Expression<Func<IDataEventSink<Food>, Task>> sink)
    {
        _sideEffects.Verify(effect, Times.Once);
        _events.Verify(sink, Times.Once);
    }

    private void VerifyCreated() =>
        VerifyBoth(
            s => s.OnCreatedAsync("food", It.IsAny<IReadOnlyList<Food>>(), It.IsAny<WriteEffectOptions?>(), It.IsAny<CancellationToken>()),
            e => e.OnCreatedAsync(It.IsAny<IReadOnlyList<Food>>(), It.IsAny<CancellationToken>()));

    private void VerifyUpdated() =>
        VerifyBoth(
            s => s.OnUpdatedAsync("food", It.IsAny<Food>(), It.IsAny<WriteEffectOptions?>(), It.IsAny<CancellationToken>()),
            e => e.OnUpdatedAsync(It.IsAny<Food>(), It.IsAny<CancellationToken>()));

    private void VerifyDeleted() =>
        VerifyBoth(
            s => s.OnDeletedAsync<Food>("food", _stored, It.IsAny<WriteEffectOptions?>(), It.IsAny<CancellationToken>()),
            e => e.OnDeletedAsync(_stored, It.IsAny<CancellationToken>()));

    [Fact]
    public async Task A_v1_create_fires_the_side_effects_and_the_event()
    {
        await V1Controller().CreateFood(Json("""{"name":"Apple"}"""));

        VerifyCreated();
    }

    [Fact]
    public async Task A_v3_create_fires_the_side_effects_and_the_event()
    {
        await V3Controller().CreateFood(Json("""{"name":"Apple","date":1727654400000}"""));

        VerifyCreated();
    }

    [Fact]
    public async Task A_v1_update_fires_the_side_effects_and_the_event()
    {
        await V1Controller().UpdateFood("stored", new Food { Name = "Apple", Type = "food" });

        VerifyUpdated();
    }

    [Fact]
    public async Task A_v3_update_fires_the_side_effects_and_the_event()
    {
        await V3Controller().UpdateFood("stored", Json("""{"name":"Apple","date":1727654400000}"""));

        VerifyUpdated();
    }

    [Fact]
    public async Task A_v1_delete_fires_the_side_effects_and_the_event()
    {
        await V1Controller().DeleteFood("stored");

        VerifyDeleted();
    }

    [Fact]
    public async Task A_v3_delete_fires_the_side_effects_and_the_event()
    {
        await V3Controller().DeleteFood("stored");

        VerifyDeleted();
    }

    [Fact]
    public async Task A_v1_bulk_delete_fires_the_side_effects_and_the_event()
    {
        await V1Controller("?find[name]=Apple").DeleteFoodByFilter();

        VerifyBoth(
            s => s.OnBulkDeletedAsync("food", 2, It.IsAny<WriteEffectOptions?>(), It.IsAny<CancellationToken>()),
            e => e.OnBulkDeletedAsync(2, It.IsAny<CancellationToken>()));
        _sideEffects.Verify(
            s => s.OnDeletedAsync(It.IsAny<string>(), It.IsAny<Food?>(), It.IsAny<WriteEffectOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _events.Verify(e => e.OnDeletedAsync(It.IsAny<Food?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_v1_delete_of_a_missing_food_fires_nothing()
    {
        _repository
            .Setup(r => r.DeleteFoodAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await V1Controller().DeleteFood("missing");

        _sideEffects.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_v1_create_stores_text_as_sent()
    {
        await V1Controller().CreateFood(Json("""{"name":" Mac & Cheese "}"""));

        _written.Should().ContainSingle().Which.Name.Should().Be(" Mac & Cheese ");
    }

    [Fact]
    public async Task A_v3_create_stores_text_as_sent()
    {
        await V3Controller().CreateFood(Json("""{"name":"Mac & Cheese","date":1727654400000}"""));

        _written.Should().ContainSingle().Which.Name.Should().Be("Mac & Cheese");
    }

    [Fact]
    public async Task A_native_create_sanitizes_text()
    {
        await FoodServices.Over(_repository.Object)
            .CreateFoodAsync([new Food { Name = "Mac & Cheese" }], FoodWriteMode.Native);

        _written.Should().ContainSingle().Which.Name.Should().Be("Mac &amp; Cheese");
    }

    [Fact]
    public async Task A_v1_create_applies_the_unit_and_gi_defaults()
    {
        await V1Controller().CreateFood(Json("""{"name":"Apple","unit":"","gi":5}"""));

        var food = _written.Should().ContainSingle().Subject;
        food.Unit.Should().Be("g");
        food.Gi.Should().Be(2);
    }

    [Fact]
    public async Task A_v3_create_stores_unit_and_gi_as_sent()
    {
        await V3Controller().CreateFood(Json("""{"name":"Apple","unit":"","gi":5,"date":1727654400000}"""));

        var food = _written.Should().ContainSingle().Subject;
        food.Unit.Should().BeNullOrEmpty();
        food.Gi.Should().Be(5);
    }
}
