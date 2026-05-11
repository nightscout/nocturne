import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("$app/navigation", () => ({
  beforeNavigate: vi.fn(),
}));

vi.mock("runed", () => ({
  Debounced: class {
    current: unknown;
    constructor(fn: () => unknown, _delay: number) {
      this.current = fn();
    }
  },
}));

import { beforeNavigate } from "$app/navigation";
import { z } from "zod";
import { FormGuard } from "./form-guard.svelte";

const schema = z.object({
  name: z.string().min(2, "Name must be at least 2 characters"),
  age: z.number().min(0, "Age must be non-negative"),
});

type FormValues = z.infer<typeof schema>;

function createMockForm() {
  return {
    pending: 0,
    result: null as any,
    enhance(cb: any) {
      return { action: "/mock", method: "POST" };
    },
    for(key: string) {
      return this;
    },
  };
}

describe("FormGuard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe("dirty tracking", () => {
    it("is not dirty when values match snapshot", () => {
      const initial = { name: "Alice", age: 30 };
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => initial,
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.dirty).toBe(false);
    });

    it("is dirty when values differ from snapshot", () => {
      const initial = { name: "Alice", age: 30 };
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => initial,
        values: () => ({ name: "Bob", age: 30 }),
      });

      expect(guard.dirty).toBe(true);
    });

    it("is not dirty when snapshot is null", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => null,
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.dirty).toBe(false);
    });
  });

  describe("validation", () => {
    it("returns true for valid values", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.validate()).toBe(true);
      expect(guard.issues).toHaveLength(0);
      expect(guard.valid).toBe(true);
    });

    it("returns false for invalid values and populates issues", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "A", age: -1 }),
      });

      expect(guard.validate()).toBe(false);
      expect(guard.issues.length).toBeGreaterThanOrEqual(2);
      expect(guard.valid).toBe(false);
    });
  });

  describe("issuesFor", () => {
    it("returns issues filtered by field path", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "A", age: -1 }),
      });

      guard.validate();

      const nameIssues = guard.issuesFor("name");
      const ageIssues = guard.issuesFor("age");

      expect(nameIssues.length).toBe(1);
      expect(nameIssues[0].message).toContain("2 characters");
      expect(ageIssues.length).toBe(1);
      expect(ageIssues[0].message).toContain("non-negative");
    });

    it("returns empty array for fields with no issues", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
      });

      guard.validate();
      expect(guard.issuesFor("name")).toHaveLength(0);
    });
  });

  describe("reset", () => {
    it("clears issues and calls onreset with snapshot", () => {
      const onreset = vi.fn();
      const initial = { name: "Alice", age: 30 };
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => initial,
        values: () => ({ name: "A", age: -1 }),
        onreset,
      });

      guard.validate();
      expect(guard.issues.length).toBeGreaterThan(0);

      guard.reset();

      expect(guard.issues).toHaveLength(0);
      expect(guard.touched).toBe(false);
      expect(onreset).toHaveBeenCalledWith(initial);
    });
  });

  describe("touched", () => {
    it("starts as false", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.touched).toBe(false);
    });
  });

  describe("snapshot", () => {
    it("captures initial values as snapshot", () => {
      const initial = { name: "Alice", age: 30 };
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => initial,
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.snapshot).toEqual(initial);
    });

    it("snapshot is null when initial returns null", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => null,
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.snapshot).toBeNull();
    });
  });

  describe("navigation blocking", () => {
    it("registers beforeNavigate when navBlockMessage is provided", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
        navBlockMessage: "Unsaved changes will be lost",
      });

      expect(beforeNavigate).toHaveBeenCalledTimes(1);
    });

    it("does not register beforeNavigate when no navBlockMessage", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(beforeNavigate).not.toHaveBeenCalled();
    });
  });

  describe("enhance", () => {
    it("returns enhance attributes from form", () => {
      const mockForm = createMockForm();
      const guard = new FormGuard({
        form: mockForm,
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
      });

      const result = guard.enhance();
      expect(result).toEqual({ action: "/mock", method: "POST" });
    });
  });

  describe("submitted", () => {
    it("starts as false", () => {
      const guard = new FormGuard({
        form: createMockForm(),
        schema,
        el: () => null,
        initial: () => ({ name: "Alice", age: 30 }),
        values: () => ({ name: "Alice", age: 30 }),
      });

      expect(guard.submitted).toBe(false);
    });
  });
});
