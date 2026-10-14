import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { page as appState } from "$app/state";

interface TherapyFixture {
  profileName: string;
  isDefault?: boolean;
  isExternallyManaged?: boolean;
  enteredBy?: string;
  dataSource?: string;
  createdAt?: string;
  units?: string;
  timezone?: string;
  dia?: number;
  carbsHr?: number;
  delay?: number;
}

let therapySettings: TherapyFixture[] = [];
const deleted: string[] = [];

// Hoisted above every declaration in this file, so the factory may only reach out to the
// lazily-read bindings above it.
vi.mock("$api/generated/profiles.generated.remote", () => ({
  getProfileSummary: () => ({
    current: {
      therapySettings,
      basalSchedules: [],
      carbRatioSchedules: [],
      sensitivitySchedules: [],
      targetRangeSchedules: [],
    },
    loading: false,
    error: undefined,
  }),
  setDefaultProfile: () => Promise.resolve(),
  deleteProfileByName: (name: string) => {
    deleted.push(name);
    return Promise.resolve();
  },
  createTargetRangeSchedule: () => Promise.resolve(),
}));

import ProfilePage from "./+page.svelte";

const therapy = (overrides: TherapyFixture): TherapyFixture => ({
  createdAt: "2026-09-01T00:00:00Z",
  units: "mg/dL",
  timezone: "UTC",
  dia: 5,
  carbsHr: 20,
  delay: 20,
  ...overrides,
});

// The stub's `url` is branded with the route union, so the query string is edited in
// place rather than swapped for a fresh URL.
/** Selects a profile the way the page does, through the `name` query parameter. */
const select = (name: string) => {
  appState.url.search = `?name=${encodeURIComponent(name)}`;
};

const deleteButton = () => page.getByRole("button", { name: "Delete", exact: true });

beforeEach(() => {
  therapySettings = [];
  deleted.length = 0;
  appState.url.search = "";
});

describe("settings/profile delete", () => {
  it("offers delete for a profile that is neither active nor the last one", async () => {
    therapySettings = [
      therapy({ profileName: "Default" }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("Default");

    render(ProfilePage, {});

    await expect.element(deleteButton()).toBeEnabled();
  });

  it("blocks deleting the active profile and says why", async () => {
    therapySettings = [
      therapy({ profileName: "Default" }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("default");

    render(ProfilePage, {});

    await expect.element(deleteButton()).toBeDisabled();
    await expect
      .element(page.getByText(/active profile\. Set another profile as active first/))
      .toBeVisible();
  });

  it("blocks deleting the only profile and says why", async () => {
    therapySettings = [therapy({ profileName: "Default", isDefault: true })];

    render(ProfilePage, {});

    await expect.element(deleteButton()).toBeDisabled();
    await expect
      .element(page.getByText(/only profile\. Deleting it would leave/))
      .toBeVisible();
  });

  /**
   * The profile the delete exists for is relayed by a connector, so removing the stored copy
   * does not stop it arriving again. Saying so is the difference between a fix and a loop.
   */
  it("warns that a synced profile will be sent again, naming its source", async () => {
    therapySettings = [
      therapy({
        profileName: "Default",
        isExternallyManaged: true,
        enteredBy: "Loop",
        dataSource: "nightscout-connector",
      }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("Default");

    render(ProfilePage, {});
    await deleteButton().click();

    await expect
      .element(page.getByText(/nightscout-connector will send this profile again/))
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: /Manage data sources/ }))
      .toBeVisible();
  });

  it("falls back to the entering app when no data source is recorded", async () => {
    therapySettings = [
      therapy({ profileName: "Default", isExternallyManaged: true, enteredBy: "Loop" }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("Default");

    render(ProfilePage, {});
    await deleteButton().click();

    await expect
      .element(page.getByText(/Loop will send this profile again/))
      .toBeVisible();
  });

  it("does not warn about re-creation for a profile no source manages", async () => {
    therapySettings = [
      therapy({ profileName: "Manual" }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("Manual");

    render(ProfilePage, {});
    await deleteButton().click();

    await expect.element(page.getByText(/Delete the profile/)).toBeVisible();
    expect(page.getByText(/will send this profile again/).elements()).toHaveLength(0);
  });

  it("deletes the exact name it was shown, not a differently-cased one", async () => {
    therapySettings = [
      therapy({ profileName: "Default" }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("Default");

    render(ProfilePage, {});
    await deleteButton().click();
    await page.getByRole("button", { name: "Delete profile" }).click();

    await vi.waitFor(() => expect(deleted).toEqual(["Default"]));
  });

  it("shows which source a profile was synced from", async () => {
    therapySettings = [
      therapy({ profileName: "Default", dataSource: "nightscout-connector" }),
      therapy({ profileName: "default", isDefault: true }),
    ];
    select("Default");

    render(ProfilePage, {});

    await expect
      .element(page.getByText(/Synced from nightscout-connector/))
      .toBeVisible();
  });
});
