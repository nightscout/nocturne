using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

[Trait("Category", "Unit")]
public class AlertRulesControllerSettingOptionsTests
{
    private static AlertRulesController Create()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new NocturneDbContext(options) { TenantId = Guid.NewGuid() };
        var factory = new TestNocturneDbContextFactory(db);

        return new AlertRulesController(
            factory,
            new TenantMembershipRepository(factory, AccountLinkRoutes.Off),
            new SubjectRepository(factory, AccountLinkRoutes.Off),
            new AlertRepository(factory),
            new AlertRuleRepository(factory, TimeProvider.System),
            MockTenantAccessor.Create(db.TenantId).Object,
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<IAlertRuleConditionValidator>(),
            Mock.Of<ISecretEncryptionService>(),
            Rearms.Unannounced(),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>(), Mock.Of<IUnevaluableRuleNotice>()),
            Mock.Of<ILogger<AlertRulesController>>(),
            TimeProvider.System);
    }

    [Fact]
    public void The_options_are_what_the_validators_accept()
    {
        var response = Create().GetSettingOptions().Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AlertRuleSettingOptionsResponse>().Subject;

        response.OnUnknownValues.Should().Equal(AlertRuleOnUnknown.All);
        response.OnUnknownValues.Should().Contain(response.DefaultOnUnknown);
        response.OnUnknownValues.Should().OnlyContain(v => AlertRuleOnUnknown.IsKnown(v));
        response.DefaultGlucoseFreshnessMinutes.Should().Be(AlertRuleGlucoseFreshness.DefaultMinutes);
        AlertRuleGlucoseFreshness.IsValid(response.DefaultGlucoseFreshnessMinutes).Should().BeTrue();
        AlertRuleGlucoseFreshness.IsValid(response.MinGlucoseFreshnessMinutes).Should().BeTrue();
        AlertRuleGlucoseFreshness.IsValid(response.MaxGlucoseFreshnessMinutes).Should().BeTrue();
        AlertRuleGlucoseFreshness.IsValid(response.MaxGlucoseFreshnessMinutes + 1).Should().BeFalse();
        AlertRuleGlucoseFreshness.IsValid(response.MinGlucoseFreshnessMinutes - 1).Should().BeFalse();
    }
}
