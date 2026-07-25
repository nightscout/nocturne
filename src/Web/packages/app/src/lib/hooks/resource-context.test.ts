import { describe, it, expect, vi } from "vitest";
import { ResourceContext, type ResourceRegistration } from "./resource-context.svelte";

function registration(overrides: Partial<ResourceRegistration> = {}): ResourceRegistration {
  return {
    loading: false,
    error: null,
    hasData: true,
    refreshing: false,
    errorTitle: "Error Loading Data",
    refetch: () => {},
    ...overrides,
  };
}

describe("ResourceContext", () => {
  it("reports no error and no loading with nothing registered", () => {
    const ctx = new ResourceContext();
    expect(ctx.loading).toBe(false);
    expect(ctx.error).toBeNull();
    expect(ctx.hasData).toBe(false);
    expect(ctx.errorTitle).toBe("Error Loading Data");
  });

  it("is loading while any one resource is loading", () => {
    const ctx = new ResourceContext();
    ctx.register(Symbol(), registration({ loading: true, hasData: false }));
    ctx.register(Symbol(), registration());
    expect(ctx.loading).toBe(true);
  });

  it("surfaces a failure even when a sibling registered later succeeded", () => {
    const ctx = new ResourceContext();
    const failing = Symbol();
    const succeeding = Symbol();
    ctx.register(failing, registration({ error: "actogram failed", errorTitle: "Error Loading Sleep Report", hasData: false }));
    ctx.register(succeeding, registration());
    expect(ctx.error).toBe("actogram failed");
    expect(ctx.errorTitle).toBe("Error Loading Sleep Report");
  });

  it("surfaces a failure even when a sibling registered earlier succeeded", () => {
    const ctx = new ResourceContext();
    ctx.register(Symbol(), registration());
    ctx.register(Symbol(), registration({ error: "trends failed", hasData: false }));
    expect(ctx.error).toBe("trends failed");
  });

  it("fans refetch out to every registered resource", () => {
    const ctx = new ResourceContext();
    const first = vi.fn();
    const second = vi.fn();
    ctx.register(Symbol(), registration({ refetch: first }));
    ctx.register(Symbol(), registration({ refetch: second }));
    ctx.refetch();
    expect(first).toHaveBeenCalledOnce();
    expect(second).toHaveBeenCalledOnce();
  });

  it("has data when any resource has data", () => {
    const ctx = new ResourceContext();
    ctx.register(Symbol(), registration({ hasData: false, loading: true }));
    ctx.register(Symbol(), registration({ hasData: true }));
    expect(ctx.hasData).toBe(true);
  });

  it("re-registering the same key replaces that resource's state only", () => {
    const ctx = new ResourceContext();
    const key = Symbol();
    ctx.register(Symbol(), registration({ loading: true, hasData: false }));
    ctx.register(key, registration({ error: "transient" }));
    ctx.register(key, registration({ error: null }));
    expect(ctx.error).toBeNull();
    expect(ctx.loading).toBe(true);
  });

  it("is refreshing when a resource reloads with a previous value still shown", () => {
    const ctx = new ResourceContext();
    ctx.register(Symbol(), registration());
    ctx.register(Symbol(), registration({ loading: true, refreshing: true }));
    expect(ctx.refreshing).toBe(true);
    expect(ctx.hasData).toBe(true);
  });

  it("is not refreshing when a loading resource has nothing to show yet", () => {
    const ctx = new ResourceContext();
    ctx.register(Symbol(), registration({ loading: true, hasData: false }));
    expect(ctx.refreshing).toBe(false);
  });

  it("drops a resource's state when it unregisters", () => {
    const ctx = new ResourceContext();
    const key = Symbol();
    ctx.register(key, registration({ error: "gone with the panel", hasData: false }));
    ctx.unregister(key);
    expect(ctx.error).toBeNull();
    expect(ctx.hasData).toBe(false);
  });
});
