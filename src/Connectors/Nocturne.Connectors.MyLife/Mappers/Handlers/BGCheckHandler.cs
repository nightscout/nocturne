using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife manual glucose events, creating BGCheck records.
/// </summary>
internal sealed class BGCheckHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.ManualGlucose;
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        if (!context.EnableManualBgSync)
            return [];

        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var glucose))
            return [];

        var bgCheck = MyLifeFactory.CreateBGCheck(ev, glucose);
        return [bgCheck];
    }
}
