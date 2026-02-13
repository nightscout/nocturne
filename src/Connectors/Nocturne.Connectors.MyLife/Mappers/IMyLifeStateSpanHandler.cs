using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
///     Handler interface for converting MyLife events to StateSpans
/// </summary>
internal interface IMyLifeStateSpanHandler
{
    /// <summary>
    ///     Determines if this handler can process the given event for StateSpan generation
    /// </summary>
    bool CanHandleStateSpan(MyLifeEvent ev);

    /// <summary>
    ///     Converts the event to StateSpans
    /// </summary>
    IEnumerable<StateSpan> HandleStateSpan(MyLifeEvent ev, MyLifeTreatmentContext context);
}