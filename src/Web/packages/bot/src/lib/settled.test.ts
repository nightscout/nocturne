import { describe, expect, it, vi } from "vitest";
import { settled } from "./settled.js";

vi.mock("./logger.js", () => ({
  createLogger: () => ({ info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() }),
}));

describe("settled", () => {
  it("runs the handler", async () => {
    const handler = vi.fn().mockResolvedValue(undefined);

    await settled("/bg", handler)("event");

    expect(handler).toHaveBeenCalledWith("event");
  });

  it("keeps a handler's failure from reaching the adapter, which would queue the update again", async () => {
    const handler = vi.fn().mockRejectedValue(new Error("Forbidden"));

    await expect(settled("/bg", handler)("event")).resolves.toBeUndefined();
  });
});
