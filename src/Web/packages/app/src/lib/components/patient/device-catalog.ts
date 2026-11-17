import type { DeviceCatalogEntry } from "#lib/api/index.js";

/** What adding {@link entry} sets up besides the device itself, or null when nothing. */
export function trackerNote(entry: DeviceCatalogEntry | undefined): string | null {
  if (entry?.cgm)
    return entry.cgm.hasSeparateTransmitter
      ? "Also adds sensor and transmitter trackers for this model."
      : "Also adds a sensor tracker for this model.";
  if (entry?.pump)
    return entry.pump.isPatch
      ? "Also adds a pod tracker for this model."
      : "Also adds infusion site and reservoir trackers for this model.";
  return null;
}
