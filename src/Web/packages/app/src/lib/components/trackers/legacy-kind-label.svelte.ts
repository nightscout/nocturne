import { LegacyConsumableKind } from "$lib/api";

/** The one name each legacy consumable has in the UI. Built per call so wuchale translates it. */
export function legacyKindLabel(kind: LegacyConsumableKind): string {
  switch (kind) {
    case LegacyConsumableKind.Sensor:
      return "Sensor";
    case LegacyConsumableKind.Site:
      return "Infusion site";
    case LegacyConsumableKind.Reservoir:
      return "Reservoir";
    case LegacyConsumableKind.PumpBattery:
      return "Pump battery";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

/** {@link legacyKindLabel} with the Nightscout name of its age, where a reader looks for that. */
export function legacyKindLabelWithNightscoutName(kind: LegacyConsumableKind): string {
  switch (kind) {
    case LegacyConsumableKind.Sensor:
      return "Sensor (SAGE)";
    case LegacyConsumableKind.Site:
      return "Infusion site (CAGE)";
    case LegacyConsumableKind.Reservoir:
      return "Reservoir (IAGE)";
    case LegacyConsumableKind.PumpBattery:
      return "Pump battery (BAGE)";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}
