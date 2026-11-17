import type { EntryCategoryId } from "./entry-categories.js";

/** The name the app shows for an entry category. */
export function entryCategoryName(kind: EntryCategoryId): string {
  switch (kind) {
    case "bolus":
      return "Insulin";
    case "carbs":
      return "Carbs";
    case "bgCheck":
      return "BG checks";
    case "note":
      return "Notes";
    case "deviceEvent":
      return "Device events";
    case "basalInjection":
      return "Long-acting injection";
  }
}
