using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertDeviceEventRequest"/> for the V4 device event upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>The header caps of <see cref="RecordColumnRules"/>.</description></item>
/// <item><description>EventType must be a valid <see cref="Core.Models.DeviceEventType"/> enum value.</description></item>
/// <item><description>Notes capped at 10,000 characters.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertDeviceEventRequest"/>
/// <seealso cref="Controllers.V4.Devices.DeviceEventController"/>
public class UpsertDeviceEventRequestValidator : AbstractValidator<UpsertDeviceEventRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertDeviceEventRequestValidator"/> class
    /// and configures all validation rules for device event upserts.
    /// </summary>
    public UpsertDeviceEventRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.EventType).IsInEnum().WithMessage("EventType must be a valid device event type");
        RuleFor(x => x.Notes).MaximumLength(10000).When(x => x.Notes is not null);
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
    }
}
