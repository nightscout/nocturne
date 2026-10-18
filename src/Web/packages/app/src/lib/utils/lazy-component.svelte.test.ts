import { describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { lazyComponent } from "./lazy-component.svelte";

const Stub = (() => {}) as never;

function setup(loader: () => Promise<{ default: never }>) {
  let lazy!: ReturnType<typeof lazyComponent<never>>;
  const cleanup = $effect.root(() => {
    lazy = lazyComponent(loader);
  });
  flushSync();
  return {
    lazy,
    cleanup,
    setOpen(value: boolean) {
      lazy.open = value;
      flushSync();
    },
  };
}

describe("lazyComponent", () => {
  it("does not import until opened", () => {
    const loader = vi.fn(() => Promise.resolve({ default: Stub }));
    const { lazy, cleanup } = setup(loader);
    expect(loader).not.toHaveBeenCalled();
    expect(lazy.component).toBeNull();
    cleanup();
  });

  it("imports once on first open and exposes the component", async () => {
    const loader = vi.fn(() => Promise.resolve({ default: Stub }));
    const { lazy, cleanup, setOpen } = setup(loader);
    setOpen(true);
    await vi.waitFor(() => expect(lazy.component).toBe(Stub));
    expect(lazy.open).toBe(true);
    expect(loader).toHaveBeenCalledTimes(1);
    cleanup();
  });

  it("stays loaded and is not re-imported when reopened", async () => {
    const loader = vi.fn(() => Promise.resolve({ default: Stub }));
    const { lazy, cleanup, setOpen } = setup(loader);
    setOpen(true);
    await vi.waitFor(() => expect(lazy.component).toBe(Stub));
    setOpen(false);
    setOpen(true);
    expect(lazy.component).toBe(Stub);
    expect(loader).toHaveBeenCalledTimes(1);
    cleanup();
  });

  it("requests once while the import is in flight", async () => {
    let resolve!: (m: { default: never }) => void;
    const loader = vi.fn(() => new Promise<{ default: never }>((r) => (resolve = r)));
    const { lazy, cleanup, setOpen } = setup(loader);
    setOpen(true);
    setOpen(false);
    setOpen(true);
    expect(loader).toHaveBeenCalledTimes(1);
    resolve({ default: Stub });
    await vi.waitFor(() => expect(lazy.component).toBe(Stub));
    cleanup();
  });

  it("closes on a rejected import, so the owner's next open retries it", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    const failure = new Error("chunk failed");
    const loader = vi
      .fn<() => Promise<{ default: never }>>()
      .mockRejectedValueOnce(failure)
      .mockResolvedValue({ default: Stub });
    const { lazy, cleanup, setOpen } = setup(loader);
    setOpen(true);
    await vi.waitFor(() => expect(error).toHaveBeenCalledWith(expect.any(String), failure));
    flushSync();
    expect(lazy.open).toBe(false);
    expect(lazy.component).toBeNull();
    setOpen(true);
    await vi.waitFor(() => expect(lazy.component).toBe(Stub));
    expect(lazy.open).toBe(true);
    expect(loader).toHaveBeenCalledTimes(2);
    error.mockRestore();
    cleanup();
  });

  it("neither stores nor logs an import that settles after the owner is destroyed", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    let resolve!: (m: { default: never }) => void;
    let reject!: (e: unknown) => void;
    const resolving = setup(() => new Promise((r) => (resolve = r)));
    const rejecting = setup(() => new Promise((_, r) => (reject = r)));
    resolving.setOpen(true);
    rejecting.setOpen(true);
    resolving.cleanup();
    rejecting.cleanup();
    resolve({ default: Stub });
    reject(new Error("chunk failed"));
    await new Promise((r) => setTimeout(r, 0));
    expect(resolving.lazy.component).toBeNull();
    expect(rejecting.lazy.open).toBe(true);
    expect(error).not.toHaveBeenCalled();
    error.mockRestore();
  });
});
