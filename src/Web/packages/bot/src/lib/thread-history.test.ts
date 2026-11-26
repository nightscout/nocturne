import { describe, expect, it, vi } from "vitest";
import { PostgresStateAdapter } from "@chat-adapter/state-pg";
import { purgeThreadHistory } from "./thread-history.js";

const query = vi.fn();

vi.mock("@chat-adapter/state-pg", async (original) => {
  const pg = await original<typeof import("@chat-adapter/state-pg")>();
  const logger = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn(), child: vi.fn() };
  return {
    ...pg,
    createPostgresState: () => new pg.PostgresStateAdapter({ client: { query } as never, logger }),
  };
});

describe("purgeThreadHistory", () => {
  it("deletes every thread history list from the bot's state and lets go of the connection", async () => {
    query.mockResolvedValue({ rows: [] });
    const disconnect = vi.spyOn(PostgresStateAdapter.prototype, "disconnect");

    await purgeThreadHistory("postgres://nobody@localhost/state");

    expect(query).toHaveBeenCalledWith("DELETE FROM chat_state_lists WHERE list_key LIKE $1", ["msg-history:%"]);
    expect(disconnect).toHaveBeenCalledOnce();
  });

  it("lets go of the connection when the delete fails", async () => {
    query.mockImplementation(async (sql: string) => {
      if (sql.startsWith("DELETE")) throw new Error("connection reset");
      return { rows: [] };
    });
    const disconnect = vi.spyOn(PostgresStateAdapter.prototype, "disconnect");

    await expect(purgeThreadHistory("postgres://nobody@localhost/state")).rejects.toThrow("connection reset");
    expect(disconnect).toHaveBeenCalled();
  });
});
