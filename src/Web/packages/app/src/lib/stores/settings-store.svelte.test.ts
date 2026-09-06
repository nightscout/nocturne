import { describe, it, expect, vi } from "vitest";

let respond: () => Promise<unknown>;

vi.mock("$api/ui-settings.remote", () => ({
  getUiSettings: () => ({ run: () => respond() }),
}));

import { SettingsStore } from "./settings-store.svelte";

/**
 * `settings/appearance` renders its form in the `{:else}` of
 * `{#if store.isLoading}`, so a store reporting neither loading nor loaded
 * shows that form filled from empty state.
 */
describe("a settings store loading on construction", () => {
  it("reports itself loading before the deferred read starts", () => {
    respond = () => new Promise(() => {});

    const store = new SettingsStore();

    expect(store.loadingState).toBe("loading");
    expect(store.isLoading).toBe(true);
    expect(store.isLoaded).toBe(false);
  });

  it("does not read until the constructing render has finished", async () => {
    let reads = 0;
    respond = () => {
      reads += 1;
      return new Promise(() => {});
    };

    const store = new SettingsStore();

    expect(reads).toBe(0);
    expect(store.isLoading).toBe(true);

    await vi.waitUntil(() => reads === 1);
  });

  it("reaches the loaded state once the deferred read resolves", async () => {
    respond = async () => ({ devices: { units: "mmol" } });

    const store = new SettingsStore();
    await vi.waitUntil(() => store.isLoaded);

    expect(store.loadingState).toBe("success");
    expect(store.devices).toEqual({ units: "mmol" });
  });

  /**
   * The card renders this on its own, so it has to be a whole sentence with a
   * remedy either way: `getUiSettings` sanitizes to one and a reading surface
   * forwards it, while SvelteKit's own transport reason is suppressed and the
   * fallback below stands in.
   */
  it("reports a rejected read as a sentence with a remedy", async () => {
    for (const message of [
      "We couldn't load your settings. Refresh the page to try again.",
      "Failed to execute remote function",
    ]) {
      respond = async () => {
        throw { status: 500, body: { message } };
      };

      const store = new SettingsStore();
      await vi.waitUntil(() => store.hasError);

      expect(store.error).toBe(
        "We couldn't load your settings. Refresh the page to try again."
      );
      expect(store.isLoading).toBe(false);
    }
  });
});

describe("reloading a settings store", () => {
  it("waits out a read in flight and reads again", async () => {
    const pending: Array<(value: unknown) => void> = [];
    respond = () => new Promise((resolve) => pending.push(resolve));

    const store = new SettingsStore();
    await vi.waitUntil(() => pending.length === 1);

    const reloaded = store.reload();
    expect(store.loadingState).toBe("loading");

    pending[0]({ devices: { units: "mmol" } });
    await vi.waitUntil(() => pending.length === 2);
    pending[1]({ devices: { units: "mg/dL" } });
    await reloaded;

    expect(store.devices).toEqual({ units: "mg/dL" });
    expect(store.isLoaded).toBe(true);
  });
});
