using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Treatments;
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
        IDataEventSink<Food>? events = null
    ) =>
        new FoodService(
            repository,
            new DocumentProcessingService(TimeProvider.System, NullLogger<DocumentProcessingService>.Instance),
            events ?? Mock.Of<IDataEventSink<Food>>(),
            Mock.Of<IAuditContext>(),
            NullLogger<FoodService>.Instance
        );
}
