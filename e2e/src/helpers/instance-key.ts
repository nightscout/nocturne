import { createHash } from "node:crypto";

/** What the e2e stack's API accepts as the instance key, which yields the platform administrator role. */
export const instanceKeyHeaders = {
  "X-Instance-Key": createHash("sha256").update("e2e-instance-key-test-only-not-a-real-secret").digest("hex"),
  "X-Instance-Service": "e2e-setup",
};
