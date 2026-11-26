import { afterEach, describe, expect, it, vi } from "vitest";

const purgeThreadHistory = vi.fn();
vi.mock("@nocturne/bot/thread-history", () => ({ purgeThreadHistory }));

const { purgeKeptThreadHistory } = await import("./thread-history");

describe("purgeKeptThreadHistory", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.restoreAllMocks();
    purgeThreadHistory.mockReset();
  });

  it("deletes the history in the bot's state database", async () => {
    vi.stubEnv("NOCTURNE_POSTGRES_URI", "postgres://nocturne_web@db/nocturne");
    purgeThreadHistory.mockResolvedValue(undefined);

    await purgeKeptThreadHistory();

    expect(purgeThreadHistory).toHaveBeenCalledExactlyOnceWith("postgres://nocturne_web@db/nocturne");
  });

  it("does nothing when no state database is configured", async () => {
    vi.stubEnv("NOCTURNE_POSTGRES_URI", undefined);
    vi.stubEnv("DATABASE_URL", undefined);

    await purgeKeptThreadHistory();

    expect(purgeThreadHistory).not.toHaveBeenCalled();
  });

  it("warns and does not throw when the delete fails, so server start goes on", async () => {
    vi.stubEnv("NOCTURNE_POSTGRES_URI", "postgres://nocturne_web@db/nocturne");
    purgeThreadHistory.mockRejectedValue(new Error("connection refused"));
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});

    await expect(purgeKeptThreadHistory()).resolves.toBeUndefined();
    expect(warn).toHaveBeenCalledOnce();
  });
});
