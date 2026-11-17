import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { goto } from "$app/navigation";
import { page } from "$app/state";
import { useDateParams } from "./date-params.svelte";

vi.mock("$app/navigation", () => ({ goto: vi.fn(() => Promise.resolve()) }));

function targetOf(call: unknown[]): URLSearchParams {
  return new URL(String(call[0]), "http://localhost").searchParams;
}

function seedingCalls(): URLSearchParams[] {
  return vi
    .mocked(goto)
    .mock.calls.map(targetOf)
    .filter((url) => url.has("from"));
}

describe("useDateParams", () => {
  let dispose: () => void;
  let params: ReturnType<typeof useDateParams>;

  function mount(url: string, defaultDays: number) {
    page.url = new URL(url) as typeof page.url;
    vi.mocked(goto).mockClear();
    dispose = $effect.root(() => {
      params = useDateParams(defaultDays);
    });
    flushSync();
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 2, 15, 12));
  });

  afterEach(() => {
    dispose();
    vi.useRealTimers();
  });

  describe("with a range in the URL", () => {
    beforeEach(() => {
      mount(
        "http://localhost/reports?days=7&from=2026-01-01&to=2026-01-07&isDefault=true",
        7
      );
      vi.mocked(goto).mockClear();
    });

    it("changes the day range in one navigation", () => {
      params.setDayRange(30);

      expect(goto).toHaveBeenCalledTimes(1);
      const url = targetOf(vi.mocked(goto).mock.calls[0]);
      expect(url.get("days")).toBe("30");
      expect(url.get("from")).toBe("2026-02-14");
      expect(url.get("to")).toBe("2026-03-15");
      expect(url.get("isDefault")).toBe("false");
    });

    it("sets a custom range in one navigation and drops days", () => {
      params.setCustomRange("2026-02-01", "2026-02-10");

      expect(goto).toHaveBeenCalledTimes(1);
      const url = targetOf(vi.mocked(goto).mock.calls[0]);
      expect(url.has("days")).toBe(false);
      expect(url.get("from")).toBe("2026-02-01");
      expect(url.get("to")).toBe("2026-02-10");
      expect(url.get("isDefault")).toBe("false");
    });

    it("resets to the default range in one navigation", () => {
      params.setCustomRange("2026-02-01", "2026-02-10");
      vi.mocked(goto).mockClear();

      params.reset();

      expect(goto).toHaveBeenCalledTimes(1);
      const url = targetOf(vi.mocked(goto).mock.calls[0]);
      expect(url.get("days")).toBe("7");
      expect(url.get("from")).toBe("2026-03-09");
      expect(url.get("to")).toBe("2026-03-15");
      expect(url.get("isDefault")).toBe("true");
    });

    it("seeds a report default in one navigation", () => {
      params._setDefaultDayRange(30);

      expect(goto).toHaveBeenCalledTimes(1);
      const url = targetOf(vi.mocked(goto).mock.calls[0]);
      expect(url.get("days")).toBe("30");
      expect(url.get("from")).toBe("2026-02-14");
      expect(url.get("to")).toBe("2026-03-15");
      expect(url.get("isDefault")).toBe("true");
    });
  });

  describe("seeding on init", () => {
    it("adjusts a differing default in one navigation", () => {
      mount("http://localhost/reports?days=7&isDefault=true", 30);

      const calls = seedingCalls();
      expect(calls).toHaveLength(1);
      expect(calls[0].get("days")).toBe("30");
      expect(calls[0].get("from")).toBe("2026-02-14");
      expect(calls[0].get("to")).toBe("2026-03-15");
      expect(calls[0].get("isDefault")).toBe("true");
    });

    it("fills a bare URL in one navigation", () => {
      mount("http://localhost/reports", 14);

      const calls = seedingCalls();
      expect(calls).toHaveLength(1);
      expect(calls[0].get("days")).toBe("14");
      expect(calls[0].get("from")).toBe("2026-03-02");
      expect(calls[0].get("to")).toBe("2026-03-15");
      expect(calls[0].get("isDefault")).toBe("true");
    });
  });
});
