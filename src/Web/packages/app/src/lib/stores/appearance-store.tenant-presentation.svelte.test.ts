import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("mode-watcher", () => ({
  setMode: vi.fn(),
  mode: { current: "light" },
  userPrefersMode: { current: "system" },
}));

type Store = typeof import("./appearance-store.svelte");

let loads = 0;

/** A fresh store, as a new document load would construct it from what this device holds. */
async function loadStore(): Promise<Store> {
  loads += 1;
  return import(/* @vite-ignore */ `./appearance-store.svelte.ts?load=${loads}`);
}

function storedCookie(store: Store) {
  return store.parsePrefsCookie(store.readCookieFrom(document.cookie, store.PREFS_COOKIE_NAME));
}

/**
 * A guest is drawn with the tenant owner's presentation only where the guest has set nothing, and
 * nothing the guest did not choose, the owner's values or the defaults, is stored as the guest's own.
 */
describe("applyTenantPresentation", () => {
  beforeEach(() => {
    localStorage.clear();
    document.cookie = "nocturne-prefs=;path=/;max-age=0";
  });

  it("keeps a value this device holds and fills only the ones it does not", async () => {
    localStorage.setItem("nocturne-glucose-units", JSON.stringify("mg/dl"));
    const store = await loadStore();

    store.applyTenantPresentation({ glucoseUnits: "mmol", timeFormat: "24" });

    expect(store.glucoseUnits.current).toBe("mg/dl");
    expect(store.timeFormat.current).toBe("24");
  });

  it("shows the tenant's value without storing it as the guest's own", async () => {
    const store = await loadStore();
    expect(store.timeFormat.current).toBe("12");

    store.applyTenantPresentation({ timeFormat: "24" });

    expect(store.timeFormat.current).toBe("24");
    expect(localStorage.getItem("nocturne-time-format")).toBeNull();
  });

  it("persists none of the tenant's values when the guest changes another preference", async () => {
    const store = await loadStore();
    store.applyTenantPresentation({ glucoseUnits: "mmol", timeFormat: "24" });

    store.colorTheme.current = "trio";

    const cookie = storedCookie(store);
    expect(cookie).toMatchObject({ colorTheme: "trio" });
    expect(cookie).not.toHaveProperty("glucoseUnits");
    expect(cookie).not.toHaveProperty("timeFormat");
    expect(localStorage.getItem("nocturne-glucose-units")).toBeNull();
    expect(localStorage.getItem("nocturne-time-format")).toBeNull();

    store.applyTenantPresentation({ glucoseUnits: "mg/dl", timeFormat: "12" });

    expect(store.glucoseUnits.current).toBe("mg/dl");
    expect(store.timeFormat.current).toBe("12");
  });

  it("persists no default the guest never chose for a field the tenant leaves unset", async () => {
    const store = await loadStore();
    store.applyTenantPresentation({ glucoseUnits: "mmol" });

    store.colorTheme.current = "trio";

    const cookie = storedCookie(store);
    expect(cookie).toMatchObject({ colorTheme: "trio" });
    expect(cookie).not.toHaveProperty("timeFormat");
    expect(localStorage.getItem("nocturne-time-format")).toBeNull();
  });

  it("seeds only what the guest holds when reconciling with no saved preferences", async () => {
    localStorage.setItem("nocturne-color-theme", JSON.stringify("trio"));
    const store = await loadStore();

    store.reconcilePreferences(null);
    store.applyTenantPresentation({ glucoseUnits: "mmol" });

    const cookie = storedCookie(store);
    expect(cookie).toMatchObject({ colorTheme: "trio" });
    expect(cookie).not.toHaveProperty("timeFormat");
    expect(cookie).not.toHaveProperty("glucoseUnits");
  });

  it("stores the guest's change to a field showing a fallback and drops that fallback", async () => {
    const store = await loadStore();
    store.applyTenantPresentation({ timeFormat: "24" });

    store.timeFormat.current = "12";

    expect(store.timeFormat.current).toBe("12");
    expect(localStorage.getItem("nocturne-time-format")).toBe(JSON.stringify("12"));
    expect(storedCookie(store)).toMatchObject({ timeFormat: "12" });

    store.applyTenantPresentation({ timeFormat: "24" });

    expect(store.timeFormat.current).toBe("12");
  });

  it("clears the tenant's values when the viewer is no longer a guest", async () => {
    const store = await loadStore();
    store.applyTenantPresentation({ timeFormat: "24" });

    store.applyTenantPresentation(null);

    expect(store.timeFormat.current).toBe("12");
  });
});

/**
 * A share is drawn as its owner draws it, so the viewer's own base-domain cookie, which reaches the
 * share host too, decides nothing there.
 */
describe("presentOwnerPreferences", () => {
  beforeEach(() => {
    localStorage.clear();
    document.cookie = "nocturne-prefs=;path=/;max-age=0";
  });

  it("draws a field the owner left unset with the default, not the viewer's cookie", async () => {
    document.cookie = `nocturne-prefs=${encodeURIComponent(JSON.stringify({ glucoseUnits: "mmol" }))};path=/`;
    const store = await loadStore();
    expect(store.glucoseUnits.current).toBe("mmol");

    store.presentOwnerPreferences({ colorTheme: "trio" });

    expect(store.glucoseUnits.current).toBe("mg/dl");
    expect(store.colorTheme.current).toBe("trio");
  });

  it("stores none of the owner's values as the viewer's", async () => {
    const store = await loadStore();

    store.presentOwnerPreferences({ glucoseUnits: "mmol", timeFormat: "24" });
    store.colorTheme.current = "trio";

    expect(store.glucoseUnits.current).toBe("mmol");
    expect(localStorage.getItem("nocturne-glucose-units")).toBeNull();
    expect(storedCookie(store)).not.toHaveProperty("glucoseUnits");
    expect(storedCookie(store)).not.toHaveProperty("timeFormat");
  });
});
