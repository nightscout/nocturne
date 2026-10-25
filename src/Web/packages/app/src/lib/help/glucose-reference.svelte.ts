import { guide } from "./guide.svelte";
import { GLUCOSE_REFERENCE_PATH } from "./reference-path";
import type { HelpArticle } from "./documentation-types";

export function glucoseReferenceArticle(): HelpArticle {
  return {
    ...guide(
      GLUCOSE_REFERENCE_PATH,
      "Glucose ranges and report terms",
      "Look up very low, low, in range, tight range, high, and very high in one place. This also explains reading percentages and missing data.",
      "The live table uses this running server's reference models. It contains no patient readings or credentials.",
      "Appearance changes display units. Therapy shows your personal target schedule. Alert Rules controls notification thresholds. These are separate from the fixed report bands.",
      "The reference bands explain the range colors and summaries in glucose reports. Tight range is contained within in range.",
      "Check coverage before interpreting percentages. Missing data is not a normal reading. A glucose-based estimated A1c is different from a blood-test result.",
      ["/settings/appearance", "/settings/profile", "/alerts"],
      "glucose"
    ),
    helpOnly: true,
    settings: ["/settings/appearance", "/settings/profile", "/alerts"],
  };
}
