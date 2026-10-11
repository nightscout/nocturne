import { describe, it, expect, afterEach, vi } from "vitest";
import { whenIdle } from "./when-idle";

describe("whenIdle", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  describe("with requestIdleCallback", () => {
    function stubIdleCallback() {
      const idle = new Map<number, IdleRequestCallback>();
      let next = 0;
      const request = vi.fn(
        (callback: IdleRequestCallback, _options?: IdleRequestOptions) => {
          idle.set(++next, callback);
          return next;
        },
      );
      vi.stubGlobal("requestIdleCallback", request);
      vi.stubGlobal("cancelIdleCallback", (handle: number) => idle.delete(handle));
      const runIdle = () => {
        const callbacks = [...idle.values()];
        idle.clear();
        for (const callback of callbacks) {
          callback({ didTimeout: false, timeRemaining: () => 50 });
        }
      };
      return { request, runIdle };
    }

    it("runs the callback once the browser is idle, not before", () => {
      const { runIdle } = stubIdleCallback();
      const callback = vi.fn();

      whenIdle(callback);
      expect(callback).not.toHaveBeenCalled();

      runIdle();
      expect(callback).toHaveBeenCalledOnce();
    });

    it("bounds the wait with the timeout it is given", () => {
      const { request } = stubIdleCallback();

      whenIdle(() => {}, { timeout: 750 });

      expect(request).toHaveBeenCalledWith(expect.any(Function), { timeout: 750 });
    });

    it("never runs a cancelled callback", () => {
      const { runIdle } = stubIdleCallback();
      const callback = vi.fn();

      const cancel = whenIdle(callback);
      cancel();
      runIdle();

      expect(callback).not.toHaveBeenCalled();
    });
  });

  describe("without requestIdleCallback", () => {
    it("runs the callback in a later task", () => {
      vi.useFakeTimers();
      vi.stubGlobal("requestIdleCallback", undefined);
      const callback = vi.fn();

      whenIdle(callback);
      expect(callback).not.toHaveBeenCalled();

      vi.runAllTimers();
      expect(callback).toHaveBeenCalledOnce();
    });

    it("never runs a cancelled callback", () => {
      vi.useFakeTimers();
      vi.stubGlobal("requestIdleCallback", undefined);
      const callback = vi.fn();

      const cancel = whenIdle(callback);
      cancel();
      vi.runAllTimers();

      expect(callback).not.toHaveBeenCalled();
    });
  });
});
