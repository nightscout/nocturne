using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife alert events, creating Note records.
/// </summary>
internal sealed class NoteHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventType.Alert;
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        var text = ev.InformationFromDevice ?? string.Empty;
        var note = MyLifeFactory.CreateNote(ev, text, "Alert");
        return [note];
    }
}
