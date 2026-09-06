import { describe, it, expect, vi, beforeEach } from "vitest";
import { GENERIC_SUBMIT_ERROR, RATE_LIMITED_ERROR } from "./submit-error";

const error = vi.fn();
vi.mock("svelte-sonner", () => ({ toast: { error: (m: string) => error(m) } }));

const { useToastSubmission } = await import("./toast-submission.svelte");

/**
 * A rejected remote function throws SvelteKit's `HttpError` — a plain object
 * carrying the handler's `error(status, message)`, with no `Error` prototype.
 */
function httpError(status: number, message?: string) {
  return { status, body: message === undefined ? {} : { message } };
}

beforeEach(() => {
  error.mockClear();
  vi.spyOn(console, "error").mockImplementation(() => {});
});

describe("useToastSubmission", () => {
  it("toasts nothing when the action succeeds", async () => {
    const submission = useToastSubmission("Failed to delete bolus");

    await expect(submission.run(async () => {})).resolves.toBe(true);
    expect(error).not.toHaveBeenCalled();
  });

  it("shows the server's reason for a rejected action, not the fallback", async () => {
    const submission = useToastSubmission("Failed to delete bolus");

    await expect(
      submission.run(async () => {
        throw httpError(400, "That bolus is part of a closed meal.");
      })
    ).resolves.toBe(false);

    expect(error).toHaveBeenCalledWith("That bolus is part of a closed meal.");
  });

  it("falls back to the caller's sentence when the rejection carries no reason", async () => {
    const submission = useToastSubmission("Failed to delete bolus");

    await submission.run(async () => {
      throw httpError(500);
    });

    expect(error).toHaveBeenCalledWith("Failed to delete bolus");
  });

  it("answers a rate limiter from the status", async () => {
    const submission = useToastSubmission("Failed to delete bolus");

    await submission.run(async () => {
      throw httpError(429);
    });

    expect(error).toHaveBeenCalledWith(RATE_LIMITED_ERROR);
  });

  it("keeps a thrown Error's message off the toast", async () => {
    const submission = useToastSubmission(GENERIC_SUBMIT_ERROR);

    await submission.run(async () => {
      throw new Error("connect ECONNREFUSED 127.0.0.1:5432");
    });

    expect(error).toHaveBeenCalledWith(GENERIC_SUBMIT_ERROR);
  });

  it("is busy only while the action runs", async () => {
    const submission = useToastSubmission("Failed to save");
    let release: () => void = () => {};
    const blocked = new Promise<void>((resolve) => (release = resolve));

    expect(submission.busy).toBe(false);
    const run = submission.run(() => blocked);
    expect(submission.busy).toBe(true);

    release();
    await run;
    expect(submission.busy).toBe(false);
  });

  it("clears busy after a failure", async () => {
    const submission = useToastSubmission("Failed to save");

    await submission.run(async () => {
      throw httpError(500);
    });

    expect(submission.busy).toBe(false);
  });

  it("ignores a second run while one is in flight", async () => {
    const submission = useToastSubmission("Failed to save");
    const action = vi.fn(async () => {});
    let release: () => void = () => {};
    const blocked = new Promise<void>((resolve) => (release = resolve));

    const first = submission.run(() => blocked);
    await expect(submission.run(action)).resolves.toBe(false);
    expect(action).not.toHaveBeenCalled();

    release();
    await first;
  });
});
