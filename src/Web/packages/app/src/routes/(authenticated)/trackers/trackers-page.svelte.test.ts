import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { toast } from "svelte-sonner";
import { page as pageState } from "$app/state";
import type {
  TrackerDefinitionDto,
  TrackerInstanceDto,
  TrackerPresetDto,
} from "#lib/api/index.js";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const definitions: TrackerDefinitionDto[] = [
  { id: "pod", name: "Pod" },
  { id: "sensor", name: "Sensor" },
];
const active: TrackerInstanceDto[] = [
  { id: "a1", definitionId: "pod", definitionName: "Pod active", isActive: true },
  { id: "a2", definitionId: "sensor", definitionName: "Sensor active", isActive: true },
];
const history: TrackerInstanceDto[] = [
  { id: "h1", definitionId: "pod", definitionName: "Pod history" },
  { id: "h2", definitionId: "sensor", definitionName: "Sensor history" },
];
const presets: TrackerPresetDto[] = [
  { id: "p1", definitionId: "pod", name: "Pod preset" },
  { id: "p2", definitionId: "sensor", name: "Sensor preset" },
];

const server = vi.hoisted(() => ({
  deleteAnswer: (): Promise<unknown> => Promise.resolve(),
}));

vi.mock("#lib/api/generated/trackers.generated.remote.js", () => ({
  getDefinitions: () => remoteQuery(() => definitions),
  getActiveInstances: () => remoteQuery(() => active),
  getInstanceHistory: () => remoteQuery(() => history),
  getPresets: () => remoteQuery(() => presets),
  getEndEventTypes: () => remoteQuery(() => []),
  getTriggerEventTypes: () => remoteQuery(() => []),
  deleteDefinition: remoteCommand(() => server.deleteAnswer()),
  deleteInstance: remoteCommand(() => Promise.resolve()),
  deletePreset: remoteCommand(() => Promise.resolve()),
  applyPreset: remoteCommand(() => Promise.resolve()),
  createPreset: remoteCommand(() => Promise.resolve()),
  startInstance: remoteCommand(() => Promise.resolve()),
  completeInstance: remoteCommand(() => Promise.resolve()),
}));

vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));

import TrackersPage from "./+page.svelte";

const tab = (name: string) => page.getByRole("tab", { name });

async function deletePod() {
  await tab("Definitions").click();
  await page.getByRole("button", { name: "Delete Pod" }).click();
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await vi.waitFor(() =>
    expect(page.getByRole("button", { name: "Delete Pod" }).elements()).toHaveLength(0)
  );
}

describe("trackers settings page", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    pageState.data = { user: { id: "u" }, effectivePermissions: ["*"] };
    server.deleteAnswer = () => Promise.resolve();
  });

  it("takes a deleted definition's instances, history and presets off the page before the server answers", async () => {
    server.deleteAnswer = () => new Promise(() => {});
    render(TrackersPage, {});
    await expect.element(page.getByText("Pod active")).toBeVisible();

    await deletePod();

    await expect.element(page.getByRole("button", { name: "Delete Sensor" })).toBeVisible();
    await tab("Active").click();
    await expect.element(page.getByText("Sensor active")).toBeVisible();
    expect(page.getByText("Pod active").elements()).toHaveLength(0);
    await tab("History").click();
    await expect.element(page.getByText("Sensor history")).toBeVisible();
    expect(page.getByText("Pod history").elements()).toHaveLength(0);
    await tab("Presets").click();
    await expect.element(page.getByText("Sensor preset")).toBeVisible();
    expect(page.getByText("Pod preset").elements()).toHaveLength(0);
  });

  it("puts the definition and what went with it back when the server refuses", async () => {
    const error = vi.spyOn(toast, "error");
    let refuse!: (err: unknown) => void;
    server.deleteAnswer = () => new Promise((_, reject) => (refuse = reject));
    render(TrackersPage, {});
    await expect.element(page.getByText("Pod active")).toBeVisible();

    await deletePod();
    refuse(Object.assign(new Error("boom"), { status: 500 }));

    await expect.element(page.getByRole("button", { name: "Delete Pod" })).toBeVisible();
    await tab("Presets").click();
    await expect.element(page.getByText("Pod preset")).toBeVisible();
    expect(error).toHaveBeenCalledOnce();
  });
});
