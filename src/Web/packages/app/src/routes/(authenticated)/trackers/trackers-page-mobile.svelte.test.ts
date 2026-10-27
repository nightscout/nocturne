import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  CompletionReason,
  TrackerCategory,
  TrackerMode,
  type TrackerDefinitionDto,
  type TrackerInstanceDto,
  type TrackerPresetDto,
} from "#lib/api/index.js";

const data = vi.hoisted(() => ({
  definitions: [] as unknown[],
  active: [] as unknown[],
  history: [] as unknown[],
  presets: [] as unknown[],
  triggerEventTypes: [] as string[],
}));

vi.mock("#lib/api/generated/trackers.generated.remote.js", async () => {
  const { remoteFormFields } = await import("#lib/test-stubs/remote-resource.js");
  const query = (read: () => unknown) => () => ({ current: read(), refresh: async () => {} });
  const form = () => {
    const instance = { result: undefined, pending: 0, enhance: () => ({}), fields: remoteFormFields() };
    return { for: () => instance, ...instance };
  };
  return {
    getDefinitions: query(() => data.definitions),
    getActiveInstances: query(() => data.active),
    getInstanceHistory: query(() => data.history),
    getPresets: query(() => data.presets),
    getEndEventTypes: query(() => []),
    getTriggerEventTypes: query(() => data.triggerEventTypes),
    createDefinition: form(),
    updateDefinition: form(),
  };
});
vi.mock("#lib/api/generated/deviceEvents.generated.remote.js", async () => {
  const { remoteFormFields } = await import("#lib/test-stubs/remote-resource.js");
  return { create: { for: () => ({ result: undefined, enhance: () => ({}), fields: remoteFormFields() }) } };
});
vi.mock("#lib/api/generated/reservoirReports.generated.remote.js", () => ({ create: vi.fn() }));
vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));

import TrackersPage from "./+page.svelte";
import WithTooltipProvider from "#lib/test-stubs/WithTooltipProvider.test-stub.svelte";

const PHONE = 360;
const hour = 60 * 60 * 1000;

const definitions: TrackerDefinitionDto[] = [
  {
    id: "d-sensor",
    name: "Dexcom G7 sensor, left upper arm",
    category: TrackerCategory.Sensor,
    mode: TrackerMode.Duration,
    lifespanHours: 240,
    isFavorite: true,
    notificationThresholds: [{ hours: 216 }, { hours: 240 }],
  },
  {
    id: "d-reservoir",
    name: "Omnipod reservoir",
    category: TrackerCategory.Reservoir,
    mode: TrackerMode.Duration,
    lifespanHours: 72,
  },
];

const active: TrackerInstanceDto[] = [
  {
    id: "i-sensor",
    definitionId: "d-sensor",
    definitionName: "Dexcom G7 sensor, left upper arm",
    startedAt: new Date(Date.now() - 50 * hour).toISOString(),
    expectedEndAt: new Date(Date.now() + 190 * hour).toISOString(),
    ageHours: 50,
    startNotes: "Overpatch applied after the second shower of the day",
  },
  {
    id: "i-reservoir",
    definitionId: "d-reservoir",
    definitionName: "Omnipod reservoir",
    startedAt: new Date(Date.now() - 70 * hour).toISOString(),
    expectedEndAt: new Date(Date.now() - 2 * hour).toISOString(),
    ageHours: 70,
  },
];

const history: TrackerInstanceDto[] = [
  {
    id: "h-1",
    definitionId: "d-sensor",
    definitionName: "Dexcom G7 sensor, left upper arm",
    startedAt: new Date(Date.now() - 300 * hour).toISOString(),
    completedAt: new Date(Date.now() - 60 * hour).toISOString(),
    ageHours: 240,
    completionReason: CompletionReason.ReplacedEarly,
    completionNotes: "Adhesive lifted at one edge",
  },
];

const presets: TrackerPresetDto[] = [
  {
    id: "p-1",
    name: "G7 sensor (left arm)",
    definitionId: "d-sensor",
    definitionName: "Dexcom G7 sensor, left upper arm",
    defaultStartNotes: "Left arm, upper, behind the triceps",
  },
];

type Box = { left: number; right: number; top: number; bottom: number };

function overlaps(a: Box, b: Box): boolean {
  return a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;
}

const LONGEST_LABELS: Record<string, string> = {
  Active: "Activo",
  History: "Historial",
  Definitions: "Definiciones",
  Presets: "Voreinstellungen",
};

function useLongestTabLabels() {
  for (const tab of document.querySelectorAll<HTMLElement>("[role='tab']")) {
    const walker = document.createTreeWalker(tab, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const label = LONGEST_LABELS[node.textContent?.trim() ?? ""];
      if (label) node.textContent = ` ${label} `;
    }
  }
}

function pageScrollWidth(): number {
  return document.documentElement.scrollWidth;
}

function openDialog(): HTMLElement {
  const dialogs = document.querySelectorAll<HTMLElement>("[role='dialog'], [role='alertdialog']");
  expect(dialogs.length).toBe(1);
  return dialogs[0];
}

async function expectDialogFits(open: () => Promise<void>) {
  await open();
  await expect.poll(() => document.querySelectorAll("[role='dialog'], [role='alertdialog']").length).toBe(1);
  const dialog = openDialog();
  const box = dialog.getBoundingClientRect();
  expect(box.left).toBeGreaterThanOrEqual(0);
  expect(box.right).toBeLessThanOrEqual(PHONE);
  expect(dialog.scrollWidth).toBeLessThanOrEqual(dialog.clientWidth);
  expect(pageScrollWidth()).toBeLessThanOrEqual(PHONE);
  await userEvent.keyboard("{Escape}");
  await expect.poll(() => document.querySelectorAll("[role='dialog'], [role='alertdialog']").length).toBe(0);
}

describe("trackers on a phone", () => {
  const original = [window.innerWidth, window.innerHeight] as const;

  beforeEach(async () => {
    pageState.data = { user: { id: "u1" } };
    data.definitions = definitions;
    data.active = active;
    data.history = history;
    data.presets = presets;
    data.triggerEventTypes = ["Sensor Change", "Site Change", "Pump Battery Change", "Insulin Change"];
    await page.viewport(PHONE, 780);
    render(WithTooltipProvider, { props: { component: TrackersPage } });
    await expect.element(page.getByRole("tab", { name: /Definitions/ })).toBeVisible();
  });

  afterEach(async () => {
    await page.viewport(original[0], original[1]);
  });

  it("keeps each tab label inside its tab and the tabs touch-sized", () => {
    const tabs = Array.from(document.querySelectorAll<HTMLElement>("[role='tab']"));
    expect(tabs).toHaveLength(4);
    expect(new Set(tabs.map((tab) => Math.round(tab.getBoundingClientRect().left))).size).toBe(2);
    for (const tab of tabs) {
      const box = tab.getBoundingClientRect();
      expect(tab.scrollWidth, tab.textContent ?? "").toBeLessThanOrEqual(tab.clientWidth);
      expect(box.right).toBeLessThanOrEqual(PHONE);
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
  });

  it.each(["Active", "History", "Definitions", "Presets"])(
    "does not scroll sideways on the %s tab, and its buttons are touch-sized",
    async (tab) => {
      await page.getByRole("tab", { name: new RegExp(tab) }).click();
      const panel = document.querySelector<HTMLElement>("[role='tabpanel'][data-state='active']")!;
      expect(panel).not.toBeNull();
      expect(pageScrollWidth()).toBeLessThanOrEqual(PHONE);
      for (const button of panel.querySelectorAll<HTMLElement>("button")) {
        const box = button.getBoundingClientRect();
        expect(box.right, button.textContent ?? "").toBeLessThanOrEqual(PHONE);
        expect(box.height, button.textContent ?? "").toBeGreaterThanOrEqual(44);
        expect(box.width, button.textContent ?? "").toBeGreaterThanOrEqual(44);
      }
    }
  );

  it("fits every dialog inside the screen", async () => {
    await page.getByRole("tab", { name: /Definitions/ }).click();
    await expectDialogFits(() => page.getByRole("button", { name: "New definition" }).click());
    await expectDialogFits(() => page.getByRole("button", { name: "Edit Omnipod reservoir" }).click());
    await expectDialogFits(() => page.getByRole("button", { name: "Start" }).first().click());

    await page.getByRole("tab", { name: /Active/ }).click();
    await expectDialogFits(() => page.getByRole("button", { name: "Complete" }).first().click());
    await expectDialogFits(() => page.getByRole("button", { name: "Record level" }).click());

    await page.getByRole("tab", { name: /Presets/ }).click();
    await expectDialogFits(() => page.getByRole("button", { name: "New preset" }).click());
  });
});

describe.each([430, 500, 536, 600, 640, 768])("trackers at %ipx", (width) => {
  const original = [window.innerWidth, window.innerHeight] as const;

  beforeEach(async () => {
    pageState.data = { user: { id: "u1" } };
    data.definitions = definitions;
    data.active = active;
    data.history = history;
    data.presets = presets;
    await page.viewport(width, 780);
    render(WithTooltipProvider, { props: { component: TrackersPage } });
    await expect.element(page.getByRole("tab", { name: /Definitions/ })).toBeVisible();
  });

  afterEach(async () => {
    await page.viewport(original[0], original[1]);
  });

  it("fits the longest translated tab labels", () => {
    useLongestTabLabels();
    expect(page.getByRole("tab", { name: /Voreinstellungen/ }).element()).toBeTruthy();
    for (const tab of document.querySelectorAll<HTMLElement>("[role='tab']")) {
      expect(tab.scrollWidth, tab.textContent ?? "").toBeLessThanOrEqual(tab.clientWidth);
    }
    expect(pageScrollWidth()).toBeLessThanOrEqual(width);
  });

  it.each(["Active", "Definitions", "Presets"])("keeps the %s header button clear of the title", async (tab) => {
    await page.getByRole("tab", { name: new RegExp(tab) }).click();
    const panel = document.querySelector<HTMLElement>("[role='tabpanel'][data-state='active']")!;
    const header = panel.querySelector<HTMLElement>("[data-slot='card-header']")!;
    const button = header.querySelector<HTMLElement>("button")!;
    const buttonBox = button.getBoundingClientRect();
    const headerBox = header.getBoundingClientRect();
    expect(buttonBox.right).toBeLessThanOrEqual(headerBox.right - parseFloat(getComputedStyle(header).paddingRight) + 0.5);
    for (const slot of ["card-title", "card-description"]) {
      const text = header.querySelector<HTMLElement>(`[data-slot='${slot}']`)!;
      expect(overlaps(text.getBoundingClientRect(), buttonBox), slot).toBe(false);
    }
    expect(pageScrollWidth()).toBeLessThanOrEqual(width);
  });
});
