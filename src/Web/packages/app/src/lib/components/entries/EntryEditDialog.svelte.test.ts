import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";
import { formCoerce } from "#lib/api/generated/form-utils.generated.js";
import {
  CreateBolusRequestSchema,
  CreateCarbIntakeRequestSchema,
  UpdateBolusRequestSchema,
  UpdateCarbIntakeRequestSchema,
  UpsertBGCheckRequestSchema,
  UpsertNoteRequestSchema,
  UpsertDeviceEventRequestSchema,
  UpdateBasalInjectionRequestSchema,
} from "#lib/api/generated/schemas.js";
import type { EntryRecord } from "#lib/constants/entry-categories.js";

// What each form was submitted with, keyed by module and export, as the browser built it.
const { submitted, capturingForm } = vi.hoisted(() => {
  const submitted: Record<string, [string, string][]> = {};
  const capturingForm = (key: string, fields: unknown) => ({
    fields,
    pending: 0,
    result: { id: "saved" },
    enhance: (callback: (helpers: { submit: () => Promise<void> }) => Promise<void>) => ({
      onsubmit: (event: Event) => {
        event.preventDefault();
        submitted[key] = [...new FormData(event.target as HTMLFormElement)].map(([k, v]) => [k, String(v)]);
        void callback({ submit: async () => {} });
      },
    }),
  });
  return { submitted, capturingForm };
});

async function formsOf(
  original: () => Promise<Record<string, unknown>>,
  module: string,
  names: Record<string, string>,
) {
  const { remoteFormFields } = await import("#lib/test-stubs/remote-resource.js");
  const forms = Object.fromEntries(
    Object.entries(names).map(([exported, key]) => [exported, capturingForm(`${module}.${key}`, remoteFormFields())]),
  );
  return { ...(await original()), ...forms };
}

vi.mock("#lib/api/generated/bolus.generated.remote.js", (importOriginal) =>
  formsOf(importOriginal, "bolus", { create: "create", update: "update" }),
);
vi.mock("#lib/api/generated/nutritions.generated.remote.js", (importOriginal) =>
  formsOf(importOriginal, "carbs", { createCarbIntake: "create", updateCarbIntake: "update" }),
);
vi.mock("#lib/api/generated/bgChecks.generated.remote.js", (importOriginal) =>
  formsOf(importOriginal, "bgCheck", { create: "create", update: "update" }),
);
vi.mock("#lib/api/generated/notes.generated.remote.js", (importOriginal) =>
  formsOf(importOriginal, "note", { create: "create", update: "update" }),
);
vi.mock("#lib/api/generated/deviceEvents.generated.remote.js", (importOriginal) =>
  formsOf(importOriginal, "deviceEvent", { create: "create", update: "update" }),
);
vi.mock("#lib/api/generated/basalInjections.generated.remote.js", (importOriginal) =>
  formsOf(importOriginal, "basalInjection", { create: "create", update: "update" }),
);
vi.mock("#lib/api/generated/patientRecords.generated.remote.js", async () => {
  const { remoteQuery } = await import("#lib/test-stubs/remote-resource.js");
  return { getInsulins: () => remoteQuery(() => []) };
});

// Closing the dialog steps browser history back, which in the test runner leaves the test page.
vi.mock("#lib/hooks/dialog-history.svelte.js", () => ({ useDialogHistory: () => {} }));

import EntryEditDialog from "./EntryEditDialog.svelte";

/** The posted fields as kit hands them to the schema: typed by their prefix, nested by their dots. */
function posted(key: string): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [name, raw] of submitted[key] ?? []) {
    const [, prefix, path] = /^(?:(n|b):)?(.*)$/.exec(name)!;
    const value = prefix === "n" ? Number(raw) : prefix === "b" ? raw === "on" : raw;
    const keys = path.split(".");
    let target = out;
    for (const k of keys.slice(0, -1)) target = (target[k] ??= {}) as Record<string, unknown>;
    target[keys.at(-1)!] = value;
  }
  return out;
}

function issues(schema: z.ZodType, key: string) {
  return formCoerce(schema).safeParse(posted(key)).error?.issues;
}

const at = Date.UTC(2026, 9, 27, 7, 30);
const id = (n: number) => `0192f0e4-0000-7000-8000-00000000000${n}`;

describe("EntryEditDialog saves", () => {
  beforeEach(() => {
    for (const key of Object.keys(submitted)) delete submitted[key];
  });

  it("creates a meal bolus as the create schemas take it", async () => {
    render(EntryEditDialog, { open: true, entry: null, onClose: () => {} });

    await page.getByRole("button", { name: "Create" }).click();

    await vi.waitFor(() => expect(Object.keys(submitted)).toEqual(expect.arrayContaining(["bolus.create", "carbs.create"])));
    expect(posted("bolus.create")).not.toHaveProperty("mills");
    expect(issues(CreateBolusRequestSchema, "bolus.create")).toBeUndefined();
    expect(issues(CreateCarbIntakeRequestSchema, "carbs.create")).toBeUndefined();
  });

  it("saves each kind of record under request, as its update schema takes it", async () => {
    const entry: EntryRecord = { kind: "bolus", data: { id: id(1), mills: at, insulin: 2 } };
    const correlatedRecords: EntryRecord[] = [
      { kind: "carbs", data: { id: id(2), mills: at, carbs: 30 } },
      { kind: "bgCheck", data: { id: id(3), mills: at, glucose: 120 } },
      { kind: "note", data: { id: id(4), mills: at, text: "Lunch" } },
      { kind: "deviceEvent", data: { id: id(5), mills: at } },
      { kind: "basalInjection", data: { id: id(6), mills: at, units: 10 } },
    ];
    render(EntryEditDialog, { open: true, entry, correlatedRecords, onClose: () => {} });

    await page.getByRole("button", { name: "Save changes" }).click();

    const kinds = ["bolus", "carbs", "bgCheck", "note", "deviceEvent", "basalInjection"];
    await vi.waitFor(() => expect(Object.keys(submitted)).toEqual(expect.arrayContaining(kinds.map((k) => `${k}.update`))));

    const update = (request: z.ZodType) => z.object({ id: z.string(), request });
    expect(posted("bolus.update")).toMatchObject({ id: id(1), request: { insulin: 2, timestamp: new Date(at).toISOString() } });
    expect(issues(update(UpdateBolusRequestSchema), "bolus.update")).toBeUndefined();
    expect(issues(update(UpdateCarbIntakeRequestSchema), "carbs.update")).toBeUndefined();
    expect(issues(update(UpsertBGCheckRequestSchema), "bgCheck.update")).toBeUndefined();
    expect(issues(update(UpsertNoteRequestSchema), "note.update")).toBeUndefined();
    expect(issues(update(UpsertDeviceEventRequestSchema), "deviceEvent.update")).toBeUndefined();
    expect(issues(update(UpdateBasalInjectionRequestSchema), "basalInjection.update")).toBeUndefined();
  });

  it("keeps the optional fields a record already holds", async () => {
    const entry: EntryRecord = {
      kind: "bolus",
      data: {
        id: id(1),
        mills: at,
        insulin: 2,
        duration: 30,
        programmed: 2.5,
        delivered: 2,
        insulinType: "Fiasp",
        unabsorbed: 0.5,
      },
    };
    const correlatedRecords: EntryRecord[] = [
      { kind: "carbs", data: { id: id(2), mills: at, carbs: 30, carbTime: 15, absorptionTime: 180 } },
      { kind: "deviceEvent", data: { id: id(5), mills: at, notes: "New site" } },
      {
        kind: "basalInjection",
        data: { id: id(6), mills: at, units: 10, notes: "Evening", insulinContext: { patientInsulinId: id(7) } },
      },
    ];
    render(EntryEditDialog, { open: true, entry, correlatedRecords, onClose: () => {} });

    await page.getByRole("button", { name: "Save changes" }).click();

    const kinds = ["bolus", "carbs", "deviceEvent", "basalInjection"];
    await vi.waitFor(() => expect(Object.keys(submitted)).toEqual(expect.arrayContaining(kinds.map((k) => `${k}.update`))));

    expect(posted("bolus.update")).toMatchObject({
      request: { duration: 30, programmed: 2.5, delivered: 2, insulinType: "Fiasp", unabsorbed: 0.5 },
    });
    expect(posted("carbs.update")).toMatchObject({ request: { carbTime: 15, absorptionTime: 180 } });
    expect(posted("deviceEvent.update")).toMatchObject({ request: { notes: "New site" } });
    expect(posted("basalInjection.update")).toMatchObject({
      request: { notes: "Evening", patientInsulinId: id(7) },
    });

    const update = (request: z.ZodType) => z.object({ id: z.string(), request });
    expect(issues(update(UpdateBolusRequestSchema), "bolus.update")).toBeUndefined();
    expect(issues(update(UpdateCarbIntakeRequestSchema), "carbs.update")).toBeUndefined();
    expect(issues(update(UpsertDeviceEventRequestSchema), "deviceEvent.update")).toBeUndefined();
    expect(issues(update(UpdateBasalInjectionRequestSchema), "basalInjection.update")).toBeUndefined();
  });
});
