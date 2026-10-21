using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>The profile activation repository and writer over a test context's store.</summary>
public static class ProfileActivationTestDeps
{
    public static ProfileActivationRepository Repository(NocturneDbContext context) =>
        new(new TestNocturneDbContextFactory(context), Mock.Of<IAuditContext>(),
            NullLogger<ProfileActivationRepository>.Instance);

    public static ProfileActivationWriter Writer(NocturneDbContext context) =>
        new(Repository(context), TestTenantCache.Create());
}
