using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

internal interface IMyLifeTreatmentHandler
{
    bool CanHandle(MyLifeEvent ev);
    IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context);
}