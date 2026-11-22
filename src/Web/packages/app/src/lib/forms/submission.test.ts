import { afterEach, describe, it, expect, vi } from "vitest";
import { useSubmission } from "./submission.svelte";
import { genericSubmitError, signInRequiredError } from "./submit-messages.svelte";
import { confirmIdentity } from "#lib/components/auth/confirm-identity.svelte.js";

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

    expect(submission.error).toBe(genericSubmitError());
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

  it("counts only the submissions that succeeded, after onSuccess", async () => {
    const submission = useSubmission();
    vi.spyOn(console, "error").mockImplementation(() => {});

    await submission.run(async () => false);
    await submission.run(async () => {
      throw new Error("offline");
    });
    await submission.run(
      async () => true,
      () => {
        throw new Error("follow-up failed");
      }
    );
    await submission.run(async () => true, async () => false);
    expect(submission.saved).toBe(0);

    await submission.run(async () => true);
    await submission.run(async () => true);
    expect(submission.saved).toBe(2);
  });

  it("reports a failed follow-up apart from a failed submission", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const submission = useSubmission({
      fallback: "Couldn't save.",
      followUpFallback: "Saved, but the follow-up failed.",
    });

    await expect(
      submission.run(
        async () => true,
        () => {
          throw new Error("offline");
        }
      )
    ).resolves.toBe(true);
    expect(submission.error).toBe("Saved, but the follow-up failed.");
  });

  it("clears on demand", async () => {
    const submission = useSubmission();
    await submission.run(async () => {
      throw new Error("offline");
    });

    submission.clear();
    expect(submission.error).toBeNull();
  });

  describe("a write refused because the session started from a sign-in link", () => {
    const refusal = () =>
      Object.assign(new Error("http"), {
        status: 403,
        body: { message: "This session was opened from a sign-in link.", code: "sign_in_required" },
      });

    afterEach(() => {
      vi.restoreAllMocks();
    });

    it("says why and opens the sign-in prompt once", async () => {
      const prompt = vi.spyOn(confirmIdentity, "request").mockResolvedValue(false);
      const submission = useSubmission();

      await submission.run(async () => {
        throw refusal();
      });

      expect(submission.error).toBe(signInRequiredError());
      expect(prompt).toHaveBeenCalledOnce();
    });

    it("opens it for a refused follow-up save too", async () => {
      const prompt = vi.spyOn(confirmIdentity, "request").mockResolvedValue(false);
      const submission = useSubmission();

      await submission.run(
        async () => true,
        async () => {
          throw refusal();
        }
      );

      expect(prompt).toHaveBeenCalledOnce();
    });

    it("opens nothing for any other refusal", async () => {
      const prompt = vi.spyOn(confirmIdentity, "request").mockResolvedValue(false);
      const submission = useSubmission();

      await submission.run(async () => {
        throw Object.assign(new Error("http"), {
          status: 403,
          body: { message: "No.", code: "missing_scope" },
        });
      });

      expect(prompt).not.toHaveBeenCalled();
    });
  });
});
