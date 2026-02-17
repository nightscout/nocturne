using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
/// Interface for handlers that map MyLife events to granular models.
/// Each handler produces a specific model type (Bolus, CarbIntake, BGCheck, etc.)
/// </summary>
internal interface IMyLifeHandler
{
    bool CanHandle(MyLifeEvent ev);
    IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context);
}
