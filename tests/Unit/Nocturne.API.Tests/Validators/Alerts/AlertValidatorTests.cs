using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Validators.Alerts;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Validators.Alerts;

public class CreateAlertRuleRequestValidatorTests
{
    private readonly CreateAlertRuleRequestValidator _validator = new();

    private static CreateAlertRuleRequest ValidRequest() => new()
    {
        Name = "High glucose",
        ConditionType = AlertConditionType.Threshold,
        Schedules =
        [
            new CreateAlertScheduleRequest { IsDefault = true }
        ],
    };

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Null_schedules_passes()
    {
        var request = ValidRequest();
        request.Schedules = null;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Schedules);
    }

    [Fact]
    public void Empty_name_fails()
    {
        var request = ValidRequest();
        request.Name = string.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Name_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Name = new string('a', 201);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void AutoResolve_enabled_without_params_fails()
    {
        var request = ValidRequest();
        request.AutoResolveEnabled = true;
        request.AutoResolveParams = null;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AutoResolveParams);
    }

    [Fact]
    public void AutoResolve_disabled_with_null_params_passes()
    {
        var request = ValidRequest();
        request.AutoResolveEnabled = false;
        request.AutoResolveParams = null;
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.AutoResolveParams);
    }

    [Fact]
    public void Description_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Description = new string('a', 2001);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Severity_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Severity = (AlertRuleSeverity)999;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Severity);
    }

    [Fact]
    public void No_default_schedule_fails()
    {
        var request = ValidRequest();
        request.Schedules = [new CreateAlertScheduleRequest { IsDefault = false }];
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Schedules);
    }
}

public class UpdateAlertRuleRequestValidatorTests
{
    private readonly UpdateAlertRuleRequestValidator _validator = new();

    private static UpdateAlertRuleRequest ValidRequest() => new()
    {
        Name = "High glucose",
        ConditionType = AlertConditionType.Threshold,
        Schedules =
        [
            new CreateAlertScheduleRequest { IsDefault = true }
        ],
    };

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_name_fails()
    {
        var request = ValidRequest();
        request.Name = string.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void No_default_schedule_fails()
    {
        var request = ValidRequest();
        request.Schedules = [new CreateAlertScheduleRequest { IsDefault = false }];
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Schedules);
    }

    [Fact]
    public void AutoResolve_enabled_without_params_fails()
    {
        var request = ValidRequest();
        request.AutoResolveEnabled = true;
        request.AutoResolveParams = null;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.AutoResolveParams);
    }
}

public class SnoozeRequestValidatorTests
{
    private readonly SnoozeRequestValidator _validator = new();

    [Fact]
    public void Valid_snooze_passes()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 30 });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Zero_minutes_fails()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 0 });
        result.ShouldHaveValidationErrorFor(x => x.Minutes);
    }

    [Fact]
    public void Negative_minutes_fails()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = -5 });
        result.ShouldHaveValidationErrorFor(x => x.Minutes);
    }

    [Fact]
    public void Minutes_exceeding_1440_fails()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 1441 });
        result.ShouldHaveValidationErrorFor(x => x.Minutes);
    }

    [Fact]
    public void Exactly_1440_minutes_passes()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 1440 });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Exactly_1_minute_passes()
    {
        var result = _validator.TestValidate(new SnoozeRequest { Minutes = 1 });
        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class CreateAlertInviteRequestValidatorTests
{
    private readonly CreateAlertInviteRequestValidator _validator = new();

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(new CreateAlertInviteRequest
        {
            EscalationStepId = Guid.NewGuid(),
        });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_escalation_step_id_fails()
    {
        var result = _validator.TestValidate(new CreateAlertInviteRequest
        {
            EscalationStepId = Guid.Empty,
        });
        result.ShouldHaveValidationErrorFor(x => x.EscalationStepId);
    }

    [Fact]
    public void Permission_scope_exceeding_max_length_fails()
    {
        var result = _validator.TestValidate(new CreateAlertInviteRequest
        {
            EscalationStepId = Guid.NewGuid(),
            PermissionScope = new string('a', 101),
        });
        result.ShouldHaveValidationErrorFor(x => x.PermissionScope);
    }

    [Fact]
    public void Null_permission_scope_passes()
    {
        var result = _validator.TestValidate(new CreateAlertInviteRequest
        {
            EscalationStepId = Guid.NewGuid(),
            PermissionScope = null,
        });
        result.ShouldNotHaveValidationErrorFor(x => x.PermissionScope);
    }
}
