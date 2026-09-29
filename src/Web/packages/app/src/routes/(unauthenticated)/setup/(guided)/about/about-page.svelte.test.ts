import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createAttachmentKey } from "svelte/attachments";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

const state = vi.hoisted(() => ({
  record: {} as Record<string, unknown>,
  relationship: {} as { relationship?: string; patientName?: string },
  submitted: vi.fn(),
  hubRefreshed: vi.fn(),
  goto: vi.fn(),
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  goto: state.goto,
}));

/** Submits the form's own fields, so the test sees exactly what the page would send. */
function submittingForm() {
  return {
    method: "POST",
    action: "?/remote=stub",
    pending: 0,
    enhance: (callback: (helpers: { submit: () => Promise<boolean> }) => Promise<void>) => ({
      method: "POST",
      action: "?/remote=stub",
      [createAttachmentKey()]: (node: HTMLFormElement) => {
        const onsubmit = (event: SubmitEvent) => {
          event.preventDefault();
          void callback({
            submit: async () => {
              state.submitted(Object.fromEntries(new FormData(node)));
              return true;
            },
          });
        };
        node.addEventListener("submit", onsubmit);
        return () => node.removeEventListener("submit", onsubmit);
      },
    }),
  };
}

vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getPatientRecord: () => remoteQuery(() => state.record),
  updatePatientRecord: submittingForm(),
}));
vi.mock("$api/generated/bodyWeights.generated.remote", () => ({
  getBodyWeights: () => remoteQuery(() => []),
  create: vi.fn(),
}));
vi.mock("$api/generated/insulinCatalogs.generated.remote", () => ({
  getCatalog: () => remoteQuery(() => []),
}));
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => Object.assign(remoteQuery(() => undefined), { refresh: state.hubRefreshed }),
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => state.relationship),
}));

import AboutPage from "./+page.svelte";

beforeEach(() => {
  vi.clearAllMocks();
  state.record = { id: "rec-1", preferredName: "Sam", timezone: "Pacific/Auckland" };
  state.relationship = { relationship: "Caregiver", patientName: "Sam" };
});

describe("About the patient", () => {
  it("speaks of the patient by name and shows what the core saved, from the same record", async () => {
    render(AboutPage);

    await expect
      .element(page.getByTestId("about-intro"))
      .toHaveTextContent(/Printed reports show Sam's name and date of birth/);
    await expect.element(page.getByLabelText("Preferred Name")).toHaveValue("Sam");
    await expect.element(page.getByLabelText("Timezone")).toHaveTextContent("Pacific/Auckland");
  });

  it("speaks to the patient when the owner is the patient", async () => {
    state.relationship = { relationship: "Self" };
    render(AboutPage);

    await expect
      .element(page.getByTestId("about-intro"))
      .toHaveTextContent(/Everything here is optional/);
  });

  it("saves without a diabetes type and returns to the hub", async () => {
    render(AboutPage);

    await page.getByRole("button", { name: "Save" }).click();

    await expect.poll(() => state.goto.mock.calls.length).toBe(1);
    expect(state.submitted.mock.calls[0][0].diabetesType || undefined).toBeUndefined();
  });

  it("saves the record as shown and returns to the hub", async () => {
    render(AboutPage);

    await page.getByLabelText("Diabetes Type").click();
    await page.getByRole("option", { name: "Type 1" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.poll(() => state.goto.mock.calls.length).toBe(1);
    expect(state.submitted).toHaveBeenCalledWith(
      expect.objectContaining({
        diabetesType: "Type1",
        preferredName: "Sam",
        timezone: "Pacific/Auckland",
      })
    );
    expect(state.hubRefreshed).toHaveBeenCalled();
    expect(state.goto).toHaveBeenCalledWith("/setup");
  });
});
