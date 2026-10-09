import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";

const removeBolus = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
const saved = vi.hoisted(() => ({ result: true }));

vi.mock("svelte-sonner", () => ({
  toast: { success: vi.fn(), error: toastError },
}));
vi.mock("$api/generated/bolus.generated.remote", () => {
  // The component spreads enhance()'s return onto its form; a submit handler here runs
  // the callback it was given, as kit's form does after a successful round trip.
  const form = () => ({
    get result() {
      return saved.result ? { id: "new" } : undefined;
    },
    enhance: (callback: (arg: { submit: () => Promise<void> }) => Promise<void>) => ({
      onsubmit: (event: Event) => {
        event.preventDefault();
        void callback({ submit: async () => {} });
      },
    }),
  });
  return { create: form(), update: form(), remove: removeBolus };
});
vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getInsulins: () => ({ current: [] }),
}));

import type { RealtimeStore } from "$lib/stores/realtime-store.svelte";
import Wrapper from "./meal-bolus-dialog-test-wrapper.svelte";

function mount() {
  let store!: RealtimeStore;
  const onSave = vi.fn();
  render(Wrapper, {
    props: {
      meal: { boluses: [{ id: "b1", mills: Date.now(), insulin: 4 }] },
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
});
