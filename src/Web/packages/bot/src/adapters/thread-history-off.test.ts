import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Chat, type Adapter } from "chat";
import { createMemoryState } from "@chat-adapter/state-memory";
import { WhatsAppAdapter } from "@chat-adapter/whatsapp";
import { NocturneWhatsAppAdapter } from "./nocturne-whatsapp.js";

const logger = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: () => logger };
const config = {
  accessToken: "not-a-token",
  appSecret: "not-a-secret",
  phoneNumberId: "1000",
  verifyToken: "not-a-token",
  userName: "nocturne",
  logger,
};
const THREAD = "whatsapp:1000:61400000000";

async function historyAfterPost(adapter: Adapter): Promise<unknown[]> {
  const state = createMemoryState();
  const bot = new Chat({ userName: "nocturne", adapters: { whatsapp: adapter }, state, logger });
  await bot.initialize();
  await bot.thread(THREAD).post("Glucose 54 mg/dL, falling");
  return state.getList(`msg-history:${THREAD}`);
}

describe("thread history", () => {
  beforeEach(() => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({ messages: [{ id: "wamid.out" }] }))),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("is kept by the stock WhatsApp adapter", async () => {
    await expect(historyAfterPost(new WhatsAppAdapter(config))).resolves.not.toEqual([]);
  });

  it("is not kept for a message the bot posts on WhatsApp", async () => {
    await expect(historyAfterPost(new NocturneWhatsAppAdapter(config))).resolves.toEqual([]);
  });
});
