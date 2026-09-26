import { describe, it, expect, vi } from "vitest";

/**
 * A colour that cannot be resolved must cost the alert its colour, never its
 * delivery. The tokens are compile-time constants, so this is latent rather
 * than live; it is guarded because the failure mode is an undelivered alert.
 */
vi.mock("@nocturne/ui/tokens", () => ({
  statusHex: () => {
    throw new Error("Not an oklch() colour: <mangled>");
  },
  statusRgb: () => {
    throw new Error("Not an oklch() colour: <mangled>");
  },
}));

vi.mock("./logger.js", () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  }),
}));

vi.mock("../lib/logger.js", () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  }),
}));

const { withAlertAccent, currentAlertAccent } = await import("./severity.js");
const { AlertDeliveryHandler } = await import("../alerts/deliver.js");

describe("withAlertAccent when the colour cannot be resolved", () => {
  it("runs the post anyway, with no accent", () => {
    let seen: unknown = "not run";

    const result = withAlertAccent("critical", () => {
      seen = currentAlertAccent();
      return "posted";
    });

    expect(result).toBe("posted");
    expect(seen).toBeUndefined();
  });
});

describe("alert delivery when the colour cannot be resolved", () => {
  it("still delivers the alert", async () => {
    const post = vi.fn().mockResolvedValue({ id: "platform-message-1" });
    const markDelivered = vi.fn().mockResolvedValue(undefined);
    const markFailed = vi.fn().mockResolvedValue(undefined);

    const bot = { channel: () => ({ post }) } as never;
    const api = { alerts: { markDelivered, markFailed } } as never;

    await new AlertDeliveryHandler(bot, api).deliver({
      deliveryId: "delivery-1",
      channelType: "slack_channel",
      destination: "C01234ABCDE",
      tenantSlug: "acme",
      payload: {
        tenantId: "018f2a1b-3c4d-7000-8000-a1b2c3d4e5f6",
        excursionId: "33333333-3333-3333-3333-333333333333",
        ruleName: "Urgent low",
        subjectName: "Alex",
        glucoseValue: 54,
        trend: "SingleDown",
        trendRate: -1.4,
        readingTimestamp: "2026-01-01T00:00:00.000Z",
        severity: "critical",
      },
    } as never);

    expect(post).toHaveBeenCalledOnce();
    expect(markDelivered).toHaveBeenCalledExactlyOnceWith("delivery-1", {
      platformMessageId: "platform-message-1",
    });
    expect(markFailed).not.toHaveBeenCalled();
  });
});
