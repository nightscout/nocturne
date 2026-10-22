import { describe, expect, it } from "vitest";
import { flushSync } from "svelte";
import { z } from "zod";
import { FormGuard, type GuardedForm } from "./form-guard.svelte";

const schema = z.object({ name: z.string(), age: z.number() });

const form: GuardedForm = {
  enhance: () => ({ action: "/mock", method: "POST" }),
};

// `touched` is set by an effect, which runs only in the client runtime: the node suite cannot see it.
describe("FormGuard touched", () => {
  it("turns touched once the values differ from the snapshot, and stays touched", () => {
    const values = $state({ name: "Alice", age: 30 });
    let guard!: FormGuard<{ name: string; age: number }>;
    const cleanup = $effect.root(() => {
      guard = new FormGuard({
        form,
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => values,
      });
    });

    try {
      flushSync();
      expect(guard.touched).toBe(false);

      values.name = "Bob";
      flushSync();
      expect(guard.touched).toBe(true);

      values.name = "Alice";
      flushSync();
      expect(guard.dirty).toBe(false);
      expect(guard.touched).toBe(true);
    } finally {
      cleanup();
    }
  });
});
