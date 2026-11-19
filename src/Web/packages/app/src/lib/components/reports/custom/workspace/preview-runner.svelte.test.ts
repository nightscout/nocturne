import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
import { PreviewRunner } from "./preview-runner.svelte.js";

/** A send whose answers the test releases one by one. */
function controlledSend() {
  const sent: unknown[] = [];
  const waiting: ((answer: CustomReportRunResponse) => void)[] = [];
  const send = (spec: unknown) => {
    sent.push(spec);
    return new Promise<CustomReportRunResponse>((resolve) => waiting.push(resolve));
  };
  const answer = async (spec: unknown) => {
    waiting.shift()?.({ spec, errors: [] });
    await vi.advanceTimersByTimeAsync(0);
  };
  return { sent, send, answer };
}

describe("the builder's preview runner", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("waits for a pause in the changes and sends only the last", async () => {
    const { sent, send } = controlledSend();
    const runner = new PreviewRunner(send, 600);

    runner.schedule({ n: 1 });
    await vi.advanceTimersByTimeAsync(300);
    runner.schedule({ n: 2 });
    await vi.advanceTimersByTimeAsync(599);
    expect(sent).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(sent).toEqual([{ n: 2 }]);
  });

  it("keeps one run in flight and runs only the latest of the specs that came meanwhile", async () => {
    const { sent, send, answer } = controlledSend();
    const runner = new PreviewRunner(send, 10);

    runner.now({ n: 1 });
    runner.now({ n: 2 });
    runner.now({ n: 3 });
    expect(sent).toEqual([{ n: 1 }]);
    expect(runner.running).toBe(true);

    await answer({ n: 1 });
    expect(runner.ran).toBe(JSON.stringify({ n: 1 }));
    expect(sent).toEqual([{ n: 1 }, { n: 3 }]);
    await answer({ n: 3 });
    expect(runner.ran).toBe(JSON.stringify({ n: 3 }));
    expect(runner.running).toBe(false);
  });

  it("does not send a spec the last run already ran", async () => {
    const { sent, send, answer } = controlledSend();
    const runner = new PreviewRunner(send, 10);

    runner.now({ n: 1 });
    await answer({ n: 1 });
    runner.schedule({ n: 1 });
    await vi.advanceTimersByTimeAsync(20);
    expect(sent).toHaveLength(1);
  });

  it("starts nothing after it is disposed, and drops the answer of the run in flight", async () => {
    const { sent, send, answer } = controlledSend();
    const runner = new PreviewRunner(send, 10);

    runner.now({ n: 1 });
    runner.now({ n: 2 });
    runner.schedule({ n: 3 });
    runner.dispose();
    await answer({ n: 1 });
    await vi.advanceTimersByTimeAsync(50);

    expect(sent).toEqual([{ n: 1 }]);
    expect(runner.run).toBeNull();
    expect(runner.ran).toBeNull();
  });

  it("clears a failure when the member returns to the spec whose result is on screen", async () => {
    let fail = false;
    const runner = new PreviewRunner((spec) => (fail ? Promise.reject(new Error("down")) : Promise.resolve({ spec, errors: [] })), 10);

    runner.now({ n: 1 });
    await vi.advanceTimersByTimeAsync(0);
    fail = true;
    runner.schedule({ n: 2 });
    await vi.advanceTimersByTimeAsync(10);
    expect(runner.failure).not.toBeNull();

    runner.schedule({ n: 1 });
    await vi.advanceTimersByTimeAsync(10);
    expect(runner.failure).toBeNull();
    expect(runner.ran).toBe(JSON.stringify({ n: 1 }));
  });
});
