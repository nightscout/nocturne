import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { copyToClipboard } from "$lib/utils";
import { toast } from "svelte-sonner";
import { createCopyFeedback, type CopyFeedback } from "./copy-feedback.svelte";

vi.mock("$lib/utils", () => ({ copyToClipboard: vi.fn() }));
vi.mock("svelte-sonner", () => ({ toast: { error: vi.fn() } }));

const FAILURE_MESSAGE =
  "Couldn't copy to the clipboard. Copy it manually instead.";

const roots: Array<() => void> = [];

function createHarness(): CopyFeedback {
  let feedback!: CopyFeedback;
  roots.push(
    $effect.root(() => {
      feedback = createCopyFeedback();
    })
  );
  return feedback;
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.mocked(copyToClipboard).mockResolvedValue(true);
});

afterEach(() => {
  while (roots.length) roots.pop()!();
  vi.useRealTimers();
  vi.clearAllMocks();
});

describe("createCopyFeedback", () => {
  it("marks the key copied and clears it after two seconds", async () => {
    const feedback = createHarness();

    await feedback.copy("secret");

    expect(feedback.isCopied()).toBe(true);
    vi.advanceTimersByTime(1999);
    expect(feedback.isCopied()).toBe(true);
    vi.advanceTimersByTime(1);
    expect(feedback.isCopied()).toBe(false);
  });

  it("restarts the reset window when the same key is copied again", async () => {
    const feedback = createHarness();

    await feedback.copy("secret");
    vi.advanceTimersByTime(1500);
    await feedback.copy("secret");

    vi.advanceTimersByTime(1000);
    expect(feedback.isCopied()).toBe(true);
    vi.advanceTimersByTime(1000);
    expect(feedback.isCopied()).toBe(false);
  });

  it("keeps two keys independent", async () => {
    const feedback = createHarness();

    await feedback.copy("first", "one");
    vi.advanceTimersByTime(1000);
    await feedback.copy("second", "two");

    vi.advanceTimersByTime(1000);
    expect(feedback.isCopied("one")).toBe(false);
    expect(feedback.isCopied("two")).toBe(true);

    vi.advanceTimersByTime(1000);
    expect(feedback.isCopied("two")).toBe(false);
  });

  it("toasts once and leaves the key not copied on failure", async () => {
    vi.mocked(copyToClipboard).mockResolvedValueOnce(false);
    const feedback = createHarness();

    const result = await feedback.copy("secret");

    expect(result).toBe(false);
    expect(feedback.isCopied()).toBe(false);
    expect(toast.error).toHaveBeenCalledTimes(1);
    expect(toast.error).toHaveBeenCalledWith(FAILURE_MESSAGE);
  });

  it("does not toast on success", async () => {
    const feedback = createHarness();

    await feedback.copy("secret");

    expect(toast.error).not.toHaveBeenCalled();
  });
});
