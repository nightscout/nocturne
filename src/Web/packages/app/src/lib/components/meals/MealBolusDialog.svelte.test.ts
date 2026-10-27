import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";

const removeBolus = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
const saved = vi.hoisted(() => ({ result: true }));
const submitted = vi.hoisted(() => ({ fields: [] as [string, string][] }));

vi.mock("svelte-sonner", () => ({
  toast: { success: vi.fn(), error: toastError },
}));
vi.mock("#lib/api/generated/bolus.generated.remote.js", async () => {
  const { remoteFormFields } = await import("#lib/test-stubs/remote-resource.js");
  // The component spreads enhance()'s return onto its form; a submit handler here runs
  // the callback it was given, as kit's form does after a successful round trip.
  const form = () => ({
    fields: remoteFormFields(),
    get result() {
      return saved.result ? { id: "new" } : undefined;
    },
    enhance: (callback: (arg: { submit: () => Promise<void> }) => Promise<void>) => ({
      onsubmit: (event: Event) => {
        event.preventDefault();
        submitted.fields = [...new FormData(event.target as HTMLFormElement)].map(([k, v]) => [k, String(v)]);
        void callback({ submit: async () => {} });
      },
    }),
  });
  return { create: form(), update: form(), remove: removeBolus };
});
vi.mock("#lib/api/generated/patientRecords.generated.remote.js", () => ({
  getInsulins: () => ({ current: [] }),
}));

import { z } from "zod";
import { formCoerce } from "#lib/api/generated/form-utils.generated.js";
import { CreateBolusRequestSchema, UpdateBolusRequestSchema } from "#lib/api/generated/schemas.js";
import type { RealtimeStore } from "#lib/stores/realtime-store.svelte.js";

/** The posted fields as kit hands them to the schema: typed by their prefix, nested by their dots. */
function posted(): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [name, raw] of submitted.fields) {
    const [, prefix, path] = /^(?:(n|b):)?(.*)$/.exec(name)!;
    const value = prefix === "n" ? Number(raw) : prefix === "b" ? raw === "on" : raw;
    const keys = path.split(".");
    let target = out;
    for (const key of keys.slice(0, -1)) target = (target[key] ??= {}) as Record<string, unknown>;
    target[keys.at(-1)!] = value;
  }
  return out;
}
import Wrapper from "./meal-bolus-dialog-test-wrapper.svelte";

function mount(meal: { correlationId?: string; boluses?: ({ id: string; mills: number; insulin: number } & Record<string, unknown>)[] } = { correlationId: "c1" }) {
  let store!: RealtimeStore;
  const onSave = vi.fn();
  render(Wrapper, {
    props: {
      meal: { boluses: [{ id: "b1", mills: Date.now(), insulin: 4 }], ...meal },
      onstore: (s: RealtimeStore) => (store = s),
      onSave,
    },
  });
  return { store, onSave };
}

async function deleteBolus() {
  await page.getByRole("button", { name: "Delete bolus" }).click();
  await page.getByRole("button", { name: "Delete", exact: true }).click();
}

describe("MealBolusDialog", () => {
  beforeEach(() => {
    toastError.mockClear();
    saved.result = true;
    submitted.fields = [];
  });

  it("tells the realtime store a treatment changed once a delete succeeds", async () => {
    removeBolus.mockResolvedValue(undefined);
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await deleteBolus();

    await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before + 1);
  });

  it("leaves the store alone when the delete fails", async () => {
    removeBolus.mockRejectedValue(new Error("nope"));
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await deleteBolus();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before);
    expect(onSave).not.toHaveBeenCalled();
  });

  it("tells the realtime store a treatment changed once a save succeeds", async () => {
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before + 1);
  });

  it("leaves the store alone when the save fails", async () => {
    saved.result = false;
    const { store } = mount();
    const before = store.treatmentRevision;

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before);
  });

  it("posts a new bolus as the create schema takes it", async () => {
    mount({ correlationId: "0192f0e4-0000-7000-8000-000000000001" });

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(submitted.fields.length).toBeGreaterThan(0));
    const body = posted();
    expect(body).toMatchObject({ insulin: expect.any(Number), kind: "Manual", correlationId: "0192f0e4-0000-7000-8000-000000000001" });
    expect(body).not.toHaveProperty("mills");
    const parsed = formCoerce(CreateBolusRequestSchema).safeParse(body);
    expect(parsed.error?.issues, JSON.stringify(body)).toBeUndefined();
  });

  it("posts an edited bolus under request, as the update schema takes it", async () => {
    mount();

    await page.getByRole("button", { name: "Edit bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(submitted.fields.length).toBeGreaterThan(0));
    const body = posted();
    expect(body).toMatchObject({ id: "b1", request: { insulin: 4 } });
    const schema = formCoerce(z.object({ id: z.string(), request: UpdateBolusRequestSchema }));
    expect(schema.safeParse(body).error?.issues, JSON.stringify(body)).toBeUndefined();
  });

  it("keeps the optional fields an edited bolus already holds", async () => {
    const patientInsulinId = "0192f0e4-0000-7000-8000-000000000002";
    mount({
      boluses: [
        {
          id: "b1",
          mills: Date.now(),
          insulin: 4,
          programmed: 4.5,
          delivered: 4,
          duration: 30,
          insulinType: "Fiasp",
          insulinContext: { patientInsulinId },
        },
      ],
    });

    await page.getByRole("button", { name: "Edit bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(submitted.fields.length).toBeGreaterThan(0));
    const body = posted();
    expect(body).toMatchObject({
      request: { programmed: 4.5, delivered: 4, duration: 30, insulinType: "Fiasp", patientInsulinId },
    });
    const schema = formCoerce(z.object({ id: z.string(), request: UpdateBolusRequestSchema }));
    expect(schema.safeParse(body).error?.issues, JSON.stringify(body)).toBeUndefined();
  });

  it("does not offer a bolus on a meal with no correlation id", async () => {
    mount({ boluses: [] });

    await expect.element(page.getByText("No insulin records for this meal")).toBeVisible();
    await expect.element(page.getByText("You cannot add insulin here.", { exact: false })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Add bolus" })).not.toBeInTheDocument();
  });
});
