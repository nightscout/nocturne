import { describe, expect, it } from "vitest";
import type { SyncProgressEvent } from "$lib/websocket/types";
import { createTerminalRunTracker } from "./terminal-run-tracker";

function event(
  connectorId: string,
  phase: SyncProgressEvent["phase"],
  timestamp = "2026-08-21T00:00:00Z",
): SyncProgressEvent {
  return {
    connectorId,
    connectorName: connectorId,
    phase,
    errorMessage: null,
    timestamp,
    messageType: null,
    messageParams: null,
  };
}

function map(...events: SyncProgressEvent[]): Record<string, SyncProgressEvent> {
  return Object.fromEntries(events.map((e) => [e.connectorId, e]));
}

describe("createTerminalRunTracker", () => {
  it("reports nothing while every connector is still syncing", () => {
    const tracker = createTerminalRunTracker();

    expect(tracker.hasNewlyFinishedRun(map(event("glooko", "Syncing")))).toBe(
      false,
    );
  });

  it.each(["Completed", "Failed"] as const)(
    "reports a %s run once",
    (phase) => {
      const tracker = createTerminalRunTracker();
      const finished = event("glooko", phase);

      expect(tracker.hasNewlyFinishedRun(map(finished))).toBe(true);
      expect(tracker.hasNewlyFinishedRun(map(finished))).toBe(false);
    },
  );

  it("does not re-report while another connector keeps emitting progress", () => {
    const tracker = createTerminalRunTracker();
    const finished = event("glooko", "Completed");

    expect(tracker.hasNewlyFinishedRun(map(finished))).toBe(true);

    // The batch's other connector reports three more times inside the linger window.
    for (const messageNumber of [1, 2, 3]) {
      expect(
        tracker.hasNewlyFinishedRun(
          map(finished, event("dexcom", "Syncing", `2026-08-21T00:00:0${messageNumber}Z`)),
        ),
      ).toBe(false);
    }
  });

  it("reports each connector in a batch as it finishes", () => {
    const tracker = createTerminalRunTracker();
    const glooko = event("glooko", "Completed");

    expect(tracker.hasNewlyFinishedRun(map(glooko))).toBe(true);
    expect(
      tracker.hasNewlyFinishedRun(map(glooko, event("dexcom", "Failed"))),
    ).toBe(true);
  });

  it("reports a second run of the same connector inside the linger window", () => {
    const tracker = createTerminalRunTracker();
    const first = event("glooko", "Completed", "2026-08-21T00:00:00Z");
    const second = event("glooko", "Completed", "2026-08-21T00:00:01Z");

    expect(tracker.hasNewlyFinishedRun(map(first))).toBe(true);
    expect(tracker.hasNewlyFinishedRun(map(second))).toBe(true);
  });

  it("re-arms once the finished entry is cleared from the store", () => {
    const tracker = createTerminalRunTracker();
    const finished = event("glooko", "Completed");

    expect(tracker.hasNewlyFinishedRun(map(finished))).toBe(true);
    expect(tracker.hasNewlyFinishedRun({})).toBe(false);
    expect(tracker.hasNewlyFinishedRun(map(finished))).toBe(true);
  });
});
