import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type { SetupHubStatus } from "$api";

vi.mock("@nocturne/watercolour", async () => ({
  Artwork: (await import("$lib/test-stubs/Artwork.test-stub.svelte")).default,
  DropSurface: (await import("$lib/test-stubs/DropSurface.test-stub.svelte")).default,
  DropGroup: (await import("$lib/test-stubs/Passthrough.test-stub.svelte")).default,
  ConfirmationBackground: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
  hostSurface: () => "light",
  watchSurface: () => () => {},
  prefersReducedMotion: () => false,
}));

const hub = vi.hoisted(() => ({ status: undefined as unknown }));
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => remoteQuery(() => hub.status),
}));

const tenant = vi.hoisted(() => ({
  relationship: {} as { relationship?: string; patientName?: string },
  setUnits: vi.fn(),
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => tenant.relationship),
  setPatientRelationship: vi.fn(),
  getUnitsAndTimezone: () =>
    remoteQuery(() => ({ glucoseUnits: "mmol", timezone: "Pacific/Auckland" })),
  setUnitsAndTimezone: tenant.setUnits,
}));
vi.mock("$lib/stores/appearance-store.svelte", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  applyPreferences: vi.fn(),
}));

import { SetupHubItemKey, SetupHubItemState } from "$api";
import SetupHub from "./SetupHub.svelte";

const KEYS = Object.values(SetupHubItemKey);

function hubWith(resolved: number): SetupHubStatus {
  const items = KEYS.map((key, i) => ({
    key,
    state:
      i >= resolved
        ? SetupHubItemState.Open
        : i % 2 === 0
          ? SetupHubItemState.Done
          : SetupHubItemState.NotForMe,
  }));
  return {
    items,
    resolvedCount: resolved,
    openCount: KEYS.length - resolved,
    totalCount: KEYS.length,
    revision: `r${resolved}`,
    showStrip: resolved < KEYS.length,
  };
}

beforeEach(() => {
  sessionStorage.clear();
  tenant.relationship = {};
  tenant.setUnits.mockReset().mockResolvedValue({});
});

describe("SetupHub", () => {
  it("lists every item with where it stands, and counts the resolved ones", async () => {
    hub.status = hubWith(3);
    render(SetupHub);

    await expect.element(page.getByTestId("hub-progress")).toHaveTextContent(/3 of 6 set up/);
    const done = page.getByTestId("hub-item-connect-data");
    await expect.element(done).toHaveAttribute("data-state", "Done");
    await expect.element(done).toHaveAttribute("data-mark-shown", "false");
    await expect.element(done.getByText("Done")).toBeVisible();

    const setAside = page.getByTestId("hub-item-alerts");
    await expect.element(setAside.getByText("Not for me")).toBeVisible();
    await expect.element(setAside).not.toHaveAttribute("data-mark-shown");

    await expect.element(page.getByTestId("hub-item-about")).toHaveAttribute("data-state", "Open");
  });

  it("paints one stop per resolved item", async () => {
    const { fakePlayer } = await import("$lib/test-stubs/Artwork.test-stub.svelte");
    fakePlayer.seeks = [];
    hub.status = hubWith(4);
    render(SetupHub);

    await expect.poll(() => fakePlayer.seeks.at(-1)).toBe(4 / 6);
  });

  it("speaks of the patient by name", async () => {
    hub.status = hubWith(0);
    tenant.relationship = { relationship: "Caregiver", patientName: "Sam" };
    render(SetupHub);

    await expect.element(page.getByRole("heading", { name: "Set Nocturne up for Sam." })).toBeVisible();
    await expect
      .element(page.getByText("Choose who else can see Sam's data."))
      .toBeVisible();
    await expect.element(page.getByTestId("basics-who")).toHaveTextContent("Sam, someone you care for");
  });

  it("opens a finished hub collapsed to all set up", async () => {
    hub.status = hubWith(6);
    render(SetupHub);

    await expect.element(page.getByTestId("hub-all-set")).toBeVisible();
    await expect.element(page.getByTestId("hub-header")).not.toBeInTheDocument();
  });

  it("paints the last stage, then washes, then collapses, when it was last seen unfinished", async () => {
    const { fakePlayer } = await import("$lib/test-stubs/Artwork.test-stub.svelte");
    Object.assign(fakePlayer.state, { mode: "live", motion: "full", progress: 0, playing: false });
    sessionStorage.setItem("nocturne.setup-hub.stop", "5");
    hub.status = hubWith(6);
    render(SetupHub);

    await expect.poll(() => fakePlayer.state.playing).toBe(true);
    // Longer than the wash: the collapse waits on the painting, not on a clock.
    await new Promise((resolve) => setTimeout(resolve, 3500));
    await expect.element(page.getByTestId("hub-header")).toBeVisible();

    fakePlayer.state.progress = 1;

    await expect
      .poll(() => page.getByTestId("hub-all-set").query(), { timeout: 5000 })
      .not.toBeNull();
  });

  it("edits the units from Basics, saying which unit therapy settings are entered in", async () => {
    hub.status = hubWith(2);
    render(SetupHub);

    await expect.element(page.getByTestId("basics-units")).toHaveTextContent(/mmol\/L/);
    await expect.element(page.getByText(/are entered in this unit/)).toBeVisible();

    await page.getByRole("button", { name: "Change" }).nth(1).click();
    await page.getByRole("radio", { name: /mg\/dL/ }).click();
    await page.getByRole("button", { name: "Save" }).click();

    expect(tenant.setUnits).toHaveBeenCalledWith({
      glucoseUnits: "mg/dl",
      timezone: "Pacific/Auckland",
    });
  });
});
