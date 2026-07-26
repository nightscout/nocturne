import { describe, it, expect, vi } from "vitest";
import { useSubmission } from "./submission.svelte";
import { GENERIC_SUBMIT_ERROR } from "./submit-error";

describe("useSubmission", () => {
  it("starts with no error", () => {
    expect(useSubmission().error).toBeNull();
  });

  it("runs onSuccess when the submission succeeded", async () => {
    const onSuccess = vi.fn();
    const submission = useSubmission();

    await expect(submission.run(async () => true, onSuccess)).resolves.toBe(true);
    expect(onSuccess).toHaveBeenCalledOnce();
    expect(submission.error).toBeNull();
  });

  it("skips onSuccess when the server returned validation issues", async () => {
    const onSuccess = vi.fn();
    const submission = useSubmission();

    await expect(submission.run(async () => false, onSuccess)).resolves.toBe(
      false
    );
    expect(onSuccess).not.toHaveBeenCalled();
    // Field issues are rendered by the form itself, so there's no form-level error.
    expect(submission.error).toBeNull();
  });

  it("turns a rejected submit into a message instead of rethrowing", async () => {
    const submission = useSubmission();

    await expect(
      submission.run(async () => {
        throw new Error("Failed to execute remote function");
      })
    ).resolves.toBe(false);

    expect(submission.error).toBe(GENERIC_SUBMIT_ERROR);
  });

  it("uses the handler's message for a 4xx rejection", async () => {
    const submission = useSubmission();

    await submission.run(async () => {
      throw Object.assign(new Error("http"), {
        status: 400,
        body: { message: "That invite has already been used." },
      });
    });

    expect(submission.error).toBe("That invite has already been used.");
  });

  it("uses the configured fallback", async () => {
    const submission = useSubmission({ fallback: "Couldn't sign you in." });

    await submission.run(async () => {
      throw new Error("offline");
    });

    expect(submission.error).toBe("Couldn't sign you in.");
  });

  it("clears the error on the next attempt", async () => {
    let fail = true;
    const submission = useSubmission();

    await submission.run(async () => {
      if (fail) throw new Error("offline");
      return true;
    });
    expect(submission.error).not.toBeNull();

    fail = false;
    await submission.run(async () => true);
    expect(submission.error).toBeNull();
  });

  it("clears on demand", async () => {
    const submission = useSubmission();
    await submission.run(async () => {
      throw new Error("offline");
    });

    submission.clear();
    expect(submission.error).toBeNull();
  });
});
