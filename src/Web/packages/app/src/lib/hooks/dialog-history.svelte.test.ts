import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { flushSync } from "svelte";

vi.mock("$app/state", async () => {
  const fake = await import("./dialog-history.test-fake.svelte");
  return { page: fake.page };
});
vi.mock("$app/navigation", async () => {
  const fake = await import("./dialog-history.test-fake.svelte");
  return { pushState: fake.pushState, replaceState: fake.replaceState };
});

import * as fake from "./dialog-history.test-fake.svelte";
import { useDialogHistory, type DialogHistoryOptions } from "./dialog-history.svelte";

const cleanups: (() => void)[] = [];

beforeEach(() => {
  fake.reset();
  vi.spyOn(history, "back").mockImplementation(() => fake.back());
});

afterEach(() => {
  cleanups.splice(0).forEach((cleanup) => cleanup());
  vi.restoreAllMocks();
});

/** Lets the fake's pending pushes and pops land, and the effects they wake run. */
async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 10));
  flushSync();
}

function dialog(initiallyOpen: boolean, options?: DialogHistoryOptions) {
  const state = $state({ open: initiallyOpen });
  const close = vi.fn(() => {
    state.open = false;
  });
  cleanups.push(
    $effect.root(() => {
      useDialogHistory(() => state.open, close, options);
    }),
  );
  flushSync();
  return {
    close,
    get open() {
      return state.open;
    },
    set open(value: boolean) {
      state.open = value;
      flushSync();
    },
  };
}

describe("useDialogHistory", () => {
  it("stays open when it mounts open, before its pushed state lands", async () => {
    const d = dialog(true);
    await settle();

    expect(d.close).not.toHaveBeenCalled();
    expect(d.open).toBe(true);
  });

  it("closes on back", async () => {
    const d = dialog(true);
    await settle();

    history.back();
    await settle();

    expect(d.close).toHaveBeenCalledOnce();
  });

  it("pops its entry when closed another way", async () => {
    const d = dialog(true);
    await settle();

    d.open = false;
    await settle();

    expect(history.back).toHaveBeenCalledOnce();
    expect(d.close).not.toHaveBeenCalled();
  });

  it("closes on back when opened from a param already in the URL", async () => {
    fake.reset("http://localhost/?edit=bolus:1");
    const d = dialog(false, { param: { name: "edit", value: () => "bolus:1" } });
    d.open = true;
    await settle();

    fake.replaceState("http://localhost/", {});
    flushSync();

    expect(d.close).toHaveBeenCalledOnce();
  });

  it("still closes on back after reopening before the last close was popped", async () => {
    const d = dialog(true);
    await settle();

    d.open = false;
    d.open = true;
    await settle();

    history.back();
    await settle();

    expect(d.close).toHaveBeenCalledOnce();
  });
});
