using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class PrimingTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId is
            MyLifeEventTypeIds.Priming
            or MyLifeEventTypeIds.TubePriming
            or MyLifeEventTypeIds.NeedlePriming;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.Priming);
        treatment.EventType = ev.EventTypeId switch
        {
            MyLifeEventTypeIds.TubePriming => MyLifeTreatmentTypes.TubePriming,
            MyLifeEventTypeIds.NeedlePriming => MyLifeTreatmentTypes.NeedlePriming,
            _ => treatment.EventType
        };

        if (MyLifeMapperHelpers.TryGetInfoDouble(
                MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice),
                MyLifeJsonKeys.PrimingAmount,
                out var amount))
            treatment.Insulin = amount;

        var treatments = new List<Treatment>
        {
            treatment
        };

        if (ev.EventTypeId == MyLifeEventTypeIds.NeedlePriming)
        {
            var siteChange = MyLifeTreatmentFactory.CreateWithSuffix(
                ev,
                MyLifeTreatmentTypes.SiteChange,
                MyLifeIdSuffixes.SiteChange
            );
            siteChange.Notes = ev.InformationFromDevice;
            treatments.Add(siteChange);
        }

        return treatments;
    }
}