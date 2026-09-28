import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";

import Finish, { type ImportResult, type SourceResult } from "./Finish.svelte";
import { PatientRelationship } from "$api";
import { patientVoice, type PatientVoice } from "$lib/onboarding/patient-voice.svelte";

const noop = () => {};

function renderFinish(
  path: "fresh" | "migration",
  {
    source = null,
    importResult = null,
    voice = patientVoice({ relationship: PatientRelationship.Self }),
  }: { source?: SourceResult; importResult?: ImportResult; voice?: PatientVoice } = {}
) {
  return render(Finish, {
    path,
    source,
    importResult,
    voice,
    onEnterDashboard: noop,
    onNavigateWithCoach: noop,
  });
}

describe("Finish", () => {
  it("does not claim a data source when none was connected", async () => {
    renderFinish("fresh");

    await expect
      .element(page.getByText(/haven't connected a data source yet/))
      .toBeVisible();
    await expect.element(page.getByText(/CGM is connected/)).not.toBeInTheDocument();
    await expect.element(page.getByText(/target range/)).not.toBeInTheDocument();
    await expect.element(page.getByText("Connect a data source")).toBeVisible();
  });

  it("says a saved connector has yet to sync", async () => {
    renderFinish("fresh", { source: "connector-saved" });

    await expect
      .element(page.getByText(/after its first sync/))
      .toBeVisible();
  });

  it("says an uploader is sending only once it is", async () => {
    renderFinish("fresh", { source: "uploader-receiving" });

    await expect
      .element(page.getByText(/is sending readings to Nocturne/))
      .toBeVisible();
  });

  it.each([
    [null, /hasn't been imported yet/],
    ["failed", /some or all of the history is missing/],
    ["running", /may still be\s+running/],
    ["partial", /not all of it/],
    ["complete", /has been copied into Nocturne/],
  ] as const)("describes a %s import as it ended", async (importResult, lead) => {
    renderFinish("migration", { importResult });

    await expect.element(page.getByText(lead)).toBeVisible();
  });

  it("welcomes the data home only after a complete import", async () => {
    renderFinish("migration", { importResult: "partial" });

    await expect.element(page.getByRole("heading", { name: /You're in/ })).toBeVisible();
    await expect.element(page.getByText(/is home/)).not.toBeInTheDocument();
  });

  it.each([
    [PatientRelationship.Self, "Your data is home.", /your uploaders keep sending/, "Open your dashboard"],
    [PatientRelationship.Caregiver, "Sam's data is home.", /Sam's uploaders keep sending/, "Open Sam's dashboard"],
    [undefined, "The data is home.", /its uploaders keep sending/, "Open the dashboard"],
  ])("words a complete import for %s", async (relationship, heading, body, button) => {
    renderFinish("migration", {
      importResult: "complete",
      voice: patientVoice({ relationship, patientName: "Sam" }),
    });

    await expect.element(page.getByRole("heading", { name: heading })).toBeVisible();
    await expect.element(page.getByText(body)).toBeVisible();
    await expect.element(page.getByRole("button", { name: button })).toBeVisible();
  });

  it("offers no public share link", async () => {
    renderFinish("fresh");

    await expect.element(page.getByText(/share link/i)).not.toBeInTheDocument();
    await expect.element(page.getByRole("checkbox")).not.toBeInTheDocument();
  });
});
