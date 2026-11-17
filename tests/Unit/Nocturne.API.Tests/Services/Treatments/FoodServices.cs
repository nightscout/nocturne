using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Controllers;

internal static class FoodServices
{
    public static IFoodService Over(
        IFoodRepository repository,
        IWriteSideEffects? sideEffects = null,
        IDataEventSink<Food>? events = null
    ) =>
        new FoodService(
            repository,
            new DocumentProcessingService(TimeProvider.System, NullLogger<DocumentProcessingService>.Instance),
            sideEffects ?? Mock.Of<IWriteSideEffects>(),
            events ?? Mock.Of<IDataEventSink<Food>>(),
            NullLogger<FoodService>.Instance
        );
}
