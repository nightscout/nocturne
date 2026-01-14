using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class SimpleMappedTreatmentHandler : IMyLifeTreatmentHandler
{
    private readonly Dictionary<int, string> _eventTypes;

    internal SimpleMappedTreatmentHandler(Dictionary<int, string> eventTypes)
    {
        _eventTypes = eventTypes;
    }

    public bool CanHandle(MyLifeEvent ev)
    {
        return _eventTypes.ContainsKey(ev.EventTypeId);
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        return _eventTypes.TryGetValue(ev.EventTypeId, out var type)
                ?
                [
                    MyLifeTreatmentFactory.Create(ev, type)
                ]
                : []
            ;
    }
}