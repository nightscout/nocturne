import { vi } from "vitest";

/**
 * The Resend adapter resolves `react` from its own install, which this package does not hoist.
 * Every module that reads the platform registry loads it; no test here exercises it.
 */
vi.mock("@resend/chat-sdk-adapter", () => ({
  createResendAdapter: () => ({ name: "resend" }),
}));
