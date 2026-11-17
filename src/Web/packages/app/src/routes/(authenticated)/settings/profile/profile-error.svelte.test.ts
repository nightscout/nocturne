import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { remoteQueryError } from "#lib/test-stubs/remote-resource.js";

vi.mock("#lib/api/generated/profiles.generated.remote.js", () => ({
  getProfileSummary: () => remoteQueryError(new Error("The profile service is down")),
  setDefaultProfile: () => Promise.resolve(),
  deleteProfileByName: () => Promise.resolve(),
  createTargetRangeSchedule: () => Promise.resolve(),
}));

import ProfilePage from "./+page.svelte";

describe("settings/profile error view", () => {
  it("has one top-level heading, and it names the failure", async () => {
    render(ProfilePage, {});

    await expect.element(page.getByRole("heading", { level: 1, name: "Could not load profiles" })).toBeVisible();
    expect(page.getByRole("heading", { level: 1 }).elements()).toHaveLength(1);
  });
});
