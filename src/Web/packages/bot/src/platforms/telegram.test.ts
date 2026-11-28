import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Chat } from "chat";
import { createMemoryState } from "@chat-adapter/state-memory";
import type { TelegramAdapter } from "@chat-adapter/telegram";
import { telegram } from "./telegram.js";

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({ info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() }),
}));

const quiet = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: () => quiet };

describe("the Telegram platform", () => {
  let calls: string[];

  beforeEach(() => {
    calls = [];
    // Telegram has no webhook registered: the answer on which the adapter's default mode starts polling.
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        const method = url.split("/").pop() ?? "";
        calls.push(method);
        const result = method === "getMe" ? { id: 1, is_bot: true, username: "nocturne_bot" } : { url: "" };
        return new Response(JSON.stringify({ ok: true, result }));
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function initialized() {
    const adapter = telegram.createAdapter({ enabled: true, botToken: "123:token", webhookSecretToken: "secret" });
    const bot = new Chat({ userName: "nocturne", adapters: { telegram: adapter }, state: createMemoryState(), logger: quiet });
    await bot.initialize();
    return adapter as TelegramAdapter;
  }

  it("takes updates from the webhook only, even when none is registered", async () => {
    const adapter = await initialized();
    try {
      expect(adapter.runtimeMode).toBe("webhook");
      expect(adapter.isPolling).toBe(false);
      expect(calls).not.toContain("getUpdates");
    } finally {
      await adapter.stopPolling();
    }
  });
});
