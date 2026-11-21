using FluentAssertions;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Treatments;

/// <summary>
/// Imported connector food entries are food records, so importing them needs <c>food.readwrite</c>.
/// </summary>
public sealed class ConnectorFoodEntriesControllerScopeTests
{
    private static readonly AuthContext Caller = new() { IsAuthenticated = true, AuthType = AuthType.DirectGrant };

    [Fact]
    public void A_caller_without_food_readwrite_is_forbidden_before_the_handler()
    {
        ActionGate.Evaluate(
                typeof(ConnectorFoodEntriesController),
                nameof(ConnectorFoodEntriesController.ImportEntries),
                new HashSet<string> { Scope.FoodRead, Scope.TreatmentsReadWrite },
                Caller)
            .ShouldBeScopeRefusal(Scope.FoodReadWrite);

        ActionGate.Evaluate(
                typeof(ConnectorFoodEntriesController),
                nameof(ConnectorFoodEntriesController.ImportEntries),
                new HashSet<string> { Scope.FoodReadWrite },
                Caller)
            .Should().BeNull("a caller holding food.readwrite reaches the handler");
    }
}
