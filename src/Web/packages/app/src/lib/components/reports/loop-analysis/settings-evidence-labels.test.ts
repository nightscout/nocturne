import { describe, expect, it } from "vitest";
import { SettingsEvidenceConfidenceReason } from "#lib/api/index.js";
import { reasonText } from "./settings-evidence-labels.svelte";

describe("reasonText", () => {
  it.each(Object.values(SettingsEvidenceConfidenceReason))("says why %s lowers the confidence and what raises it", (reason) => {
    const text = reasonText(reason);
    expect(text.why).not.toBe("");
    expect(text.why).not.toBe(reason);
    expect(text.raise).not.toBe("");
  });
});
