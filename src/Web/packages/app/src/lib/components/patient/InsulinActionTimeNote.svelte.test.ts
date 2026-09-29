import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { InsulinActionTimeSource } from "$api";
import InsulinActionTimeNote from "./InsulinActionTimeNote.svelte";

const sam = { kind: "named", name: "Sam" } as const;

describe("InsulinActionTimeNote", () => {
  it("names the primary insulin whose action time replaces the profile's", async () => {
    render(InsulinActionTimeNote, {
      actionTime: { source: InsulinActionTimeSource.PrimaryInsulin, hours: 3.5, primaryInsulinName: "Fiasp" },
      voice: { kind: "self" },
    });

    const note = page.getByTestId("action-time");
    await expect.element(note).toHaveTextContent(/action time of Fiasp, 3\.5 hours/);
    await expect.element(note).toHaveTextContent(/in place of the value in your profile/);
    await expect.element(note).toHaveTextContent(/doesn't change your pump or AID app/);
  });

  it("says an externally managed profile's time still wins over a picked insulin", async () => {
    render(InsulinActionTimeNote, {
      actionTime: { source: InsulinActionTimeSource.ExternalProfile, hours: 6, primaryInsulinName: "Fiasp" },
      voice: sam,
    });

    const note = page.getByTestId("action-time");
    await expect.element(note).toHaveTextContent(/Sam's profile is managed by another app/);
    await expect.element(note).toHaveTextContent(/its action time, 6 hours/);
    await expect.element(note).toHaveTextContent(/used instead of the action time of Fiasp/);
  });

  it("uses the profile's own value when no insulin is set, not the default", async () => {
    render(InsulinActionTimeNote, {
      actionTime: { source: InsulinActionTimeSource.Profile, hours: 5 },
      voice: { kind: "neutral" },
    });

    const note = page.getByTestId("action-time");
    await expect.element(note).toHaveTextContent(/action time in the profile, 5 hours/);
    await expect.element(note).not.toHaveTextContent(/default/);
  });

  it("names the default only when nothing sets a time", async () => {
    render(InsulinActionTimeNote, {
      actionTime: { source: InsulinActionTimeSource.Default, hours: 3 },
      voice: sam,
    });

    await expect.element(page.getByTestId("action-time")).toHaveTextContent(/default of 3 hours/);
  });
});
