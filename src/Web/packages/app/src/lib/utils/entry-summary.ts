/**
 * Text for a v4 entry record wherever a timeline row, tooltip or picker has to
 * name one in a single line.
 *
 * Amounts go through {@link formatInsulinDisplay} and {@link formatCarbDisplay}
 * so a stored value keeps its exact magnitude but is never printed at the full
 * width of a double: a bolus summed from 0.1 + 0.2 is held as
 * 0.30000000000000004 and has to read as 0.30.
 */

import type { EntryRecord } from "$lib/constants/entry-categories";
import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
import { formatCarbDisplay, formatInsulinDisplay } from "$lib/utils/formatting";

/**
 * The headline line for an entry row — the amount with its unit, or the
 * category's own name when the record carries no amount to show.
 */
export function entryLabel(entry: EntryRecord): string {
  switch (entry.kind) {
    case "bolus":
      return entry.data.insulin
        ? `${formatInsulinDisplay(entry.data.insulin)}u insulin`
        : "Bolus";
    case "carbs":
      return entry.data.carbs
        ? `${formatCarbDisplay(entry.data.carbs)}g carbs`
        : "Carbs";
    case "bgCheck":
      return entry.data.mgdl ? `${entry.data.mgdl} mg/dL` : "BG Check";
    case "note":
      return entry.data.text ?? "Note";
    case "deviceEvent":
      return entry.data.eventType ?? "Device Event";
    case "basalInjection":
      return entry.data.units
        ? `${formatInsulinDisplay(entry.data.units)}u basal`
        : "Long-acting injection";
  }
}

/** The secondary line for an entry row: the qualifier behind the amount. */
export function entryDetails(entry: EntryRecord): string {
  switch (entry.kind) {
    case "bolus":
      return entry.data.bolusType ?? "";
    case "carbs":
      return "";
    case "bgCheck":
      return entry.data.glucoseType ?? "";
    case "note":
      return entry.data.isAnnouncement ? "Announcement" : "";
    case "deviceEvent":
      return entry.data.notes ?? "";
    case "basalInjection":
      return entry.data.insulinContext?.insulinName ?? "";
  }
}

/** Longest note prefix a one-line summary carries. */
const NOTE_PREVIEW_LENGTH = 50;

/**
 * Amount and qualifier on one line, for the compact rows a dialog uses to let
 * the reader tell several entries at the same minute apart.
 */
export function entrySummary(entry: EntryRecord): string {
  const parts: string[] = [];
  switch (entry.kind) {
    case "bolus":
      if (entry.data.insulin)
        parts.push(`${formatInsulinDisplay(entry.data.insulin)}U`);
      if (entry.data.bolusType) parts.push(entry.data.bolusType);
      break;
    case "carbs":
      if (entry.data.carbs)
        parts.push(`${formatCarbDisplay(entry.data.carbs)}g carbs`);
      break;
    case "bgCheck":
      if (entry.data.mgdl) parts.push(`${entry.data.mgdl} mg/dL`);
      break;
    case "note":
      if (entry.data.text)
        parts.push(entry.data.text.slice(0, NOTE_PREVIEW_LENGTH));
      break;
    case "deviceEvent":
      if (entry.data.eventType) parts.push(entry.data.eventType);
      break;
    case "basalInjection":
      if (entry.data.units)
        parts.push(`${formatInsulinDisplay(entry.data.units)}U`);
      break;
  }
  return parts.join(" · ") || ENTRY_CATEGORIES[entry.kind].name;
}
