import { describe, expect, it, vi } from "vitest";
import type { Chat } from "chat";
import { PostgresStateAdapter } from "@chat-adapter/state-pg";
import { createMemoryState } from "@chat-adapter/state-memory";
import { purgeThreadHistory } from "./thread-history.js";

const botWith = (state: unknown) => ({ getState: () => state }) as unknown as Chat;

describe("purgeThreadHistory", () => {
  it("deletes every thread history list from the bot's Postgres state", async () => {
    const query = vi.fn().mockResolvedValue({ rows: [] });
    const logger = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() };
    const state = new PostgresStateAdapter({ client: { query } as never, logger, autoCreateSchema: false });
    vi.spyOn(state, "connect").mockResolvedValue();

    await purgeThreadHistory(botWith(state));

    expect(query).toHaveBeenCalledExactlyOnceWith("DELETE FROM chat_state_lists WHERE list_key LIKE $1", [
      "msg-history:%",
    ]);
  });

  it("does nothing for a state that is not Postgres", async () => {
    await expect(purgeThreadHistory(botWith(createMemoryState()))).resolves.toBeUndefined();
  });
});
