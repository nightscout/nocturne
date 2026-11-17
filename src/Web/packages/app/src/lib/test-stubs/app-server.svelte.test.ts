import { describe, it, expect, vi } from "vitest";
import { command, form, getRequestEvent, query } from "$app/server";
import { z } from "zod";
import { remoteQuery } from "./remote-resource";

/**
 * Only `*.svelte.test.ts` runs under vitest.browser.config.ts, which is where
 * the `$app/server` alias to this directory's stub applies — under any other
 * config the imports above resolve to the real framework and prove nothing
 * about the stub.
 *
 * What is pinned here is shape: that a query call is a resource and not the
 * implementation, that the `(schema, fn)` overloads reach `fn`, that a command
 * call is a promise carrying `updates()`, and that a form's fields answer
 * `undefined` rather than `[]` when they have no issues. Of a form's
 * submission, what is pinned is what kit does with the input: how it reads the
 * controls, the `id` a keyed instance adds, and the schema's issues. Reactivity
 * beyond that and per-argument caching are deliberately absent from the stub, so
 * nothing below asserts them.
 */
/** Submits a form built from `controls`, named as `as()` would name them, and answers what `submit()` resolved. */
function submitted(
  instance: { enhance: (callback: (helpers: { submit: () => Promise<boolean> }) => Promise<void>) => object },
  controls: Record<string, string>
): Promise<boolean> {
  return new Promise((resolve, reject) => {
    const element = document.createElement("form");
    Object.assign(
      element,
      instance.enhance(async ({ submit }) => {
        submit().then(resolve, reject);
      })
    );
    for (const [name, value] of Object.entries(controls)) {
      const input = document.createElement("input");
      Object.assign(input, { type: "hidden", name, value });
      element.append(input);
    }
    document.body.append(element);
    element.requestSubmit();
    element.remove();
  });
}

describe("$app/server stub", () => {
  it("hands back a resource rather than the implementation", async () => {
    const getValue = query(async () => 42);
    const resource = getValue();

    expect(typeof resource).not.toBe("function");
    expect(resource.loading).toBe(true);
    expect(resource.current).toBeUndefined();
    expect(resource.ready).toBe(false);

    expect(await resource).toBe(42);

    expect(resource.current).toBe(42);
    expect(resource.ready).toBe(true);
    expect(resource.loading).toBe(false);
    expect(resource.error).toBeUndefined();
  });

  it("runs the second argument of a validated query, not the first", async () => {
    const double = query("unchecked", async (n: number) => n * 2);

    expect(await double(21)).toBe(42);
  });

  it("carries a rejection on error instead of leaving it unobservable", async () => {
    const boom = query(async () => {
      throw new Error("nope");
    });
    const resource = boom();

    // Awaited, not `run()`: awaiting is the path a template takes and the one
    // that settles `error`. The framework's `run()` bypasses the instance, so
    // asserting `error` after it would pin something production does not do.
    await expect(async () => await resource).rejects.toThrow("nope");

    expect(resource.error).toBeInstanceOf(Error);
    expect(resource.loading).toBe(false);
    expect(resource.ready).toBe(false);
  });

  it("keeps a rejection nobody awaited off the unhandled-rejection channel", async () => {
    const unhandled: unknown[] = [];
    const record = (event: PromiseRejectionEvent) => {
      unhandled.push(event.reason);
      event.preventDefault();
    };
    window.addEventListener("unhandledrejection", record);

    try {
      const boom = query(async () => {
        throw new Error("unobserved");
      });
      const resource = boom();

      // Reading a getter starts the work. Nothing chains onto it, which is what
      // a component reading only `error` does.
      expect(resource.error).toBeUndefined();

      await new Promise((resolve) => setTimeout(resolve, 50));

      expect(unhandled).toEqual([]);
      expect(resource.error).toBeInstanceOf(Error);
    } finally {
      window.removeEventListener("unhandledrejection", record);
    }
  });

  it("re-runs the implementation on refresh", async () => {
    let calls = 0;
    const next = query(async () => ++calls);
    const resource = next();

    expect(await resource).toBe(1);

    await resource.refresh();

    expect(resource.current).toBe(2);
  });

  it("applies an override to the current value until it is released", async () => {
    const getValue = query(async () => 1);
    const resource = getValue();
    await resource;

    const release = resource.withOverride((value) => value + 10);
    expect(resource.current).toBe(11);

    release();
    expect(resource.current).toBe(1);
  });

  it("takes a value pushed in with set", async () => {
    const getValue = query(async () => 1);
    const resource = getValue();
    await resource;

    resource.set(7);

    expect(resource.current).toBe(7);
    expect(await resource).toBe(7);
  });

  it("hands back a command call that is a promise carrying updates", async () => {
    const send = command("unchecked", async (n: number) => n + 1);

    expect(send.pending).toBe(0);

    const call = send(1);
    expect(send.pending).toBe(1);
    expect(typeof call.updates).toBe("function");

    expect(await call.updates()).toBe(2);
    expect(send.pending).toBe(0);
  });

  it("hands back a form instance rather than the implementation", () => {
    const submit = form(z.object({ username: z.string() }), async () => ({
      ok: true,
    }));

    expect(typeof submit).not.toBe("function");
    expect(submit.method).toBe("POST");
    expect(submit.pending).toBe(0);
    expect(submit.result).toBeUndefined();
    expect(submit.enhance(async () => {})).toMatchObject({ method: "POST" });
  });

  it("spreads only the attributes a <form> takes", () => {
    const submit = form(z.object({ username: z.string() }), async () => ({
      ok: true,
    }));

    expect(Object.keys(submit)).toEqual(["method", "action"]);
  });

  it("reports no issues as undefined, the way the framework's lookup does", () => {
    const submit = form(z.object({ username: z.string() }), async () => ({
      ok: true,
    }));

    expect(submit.fields.allIssues()).toBeUndefined();
    expect(submit.fields.username.issues()).toBeUndefined();
  });

  it("submits an enhanced form with the input kit builds from its controls", async () => {
    const handler = vi.fn(async (data: unknown) => ({ saved: data }));
    const submit = form(
      z.object({ name: z.string(), count: z.number(), tags: z.array(z.string()) }),
      handler
    );

    const outcome = await submitted(submit, { name: "G7", "n:count": "2", "tags[0]": "a" });

    expect(outcome).toBe(true);
    expect(handler).toHaveBeenCalledWith({ name: "G7", count: 2, tags: ["a"] });
    expect(submit.result).toEqual({ saved: { name: "G7", count: 2, tags: ["a"] } });
  });

  it("posts a keyed instance's key as id, which a strict schema refuses", async () => {
    const handler = vi.fn();
    const submit = form(z.strictObject({ name: z.string() }), handler).for("create");

    const outcome = await submitted(submit, { name: "G7" });

    expect(outcome).toBe(false);
    expect(handler).not.toHaveBeenCalled();
    expect(submit.result).toBeUndefined();
    expect(submit.fields.allIssues()?.map((issue) => issue.message)).toEqual([
      expect.stringContaining('"id"'),
    ]);
  });

  it("rejects submit() when the handler throws", async () => {
    const submit = form(z.object({ name: z.string() }), async () => {
      throw { status: 400, body: { message: "Refused" } };
    });

    await expect(submitted(submit, { name: "G7" })).rejects.toEqual({
      status: 400,
      body: { message: "Refused" },
    });
  });

  it("refuses getRequestEvent rather than answering with a half-built event", () => {
    expect(() => getRequestEvent()).toThrow();
  });
});

describe("remoteQuery", () => {
  it("is settled before the first read, and re-reads its source", () => {
    let value = 1;
    const resource = remoteQuery(() => value);

    expect(resource.loading).toBe(false);
    expect(resource.ready).toBe(true);
    expect(resource.current).toBe(1);

    value = 2;
    expect(resource.current).toBe(2);
  });

  it("keeps re-reading its source through any number of refreshes", async () => {
    let value = 1;
    const resource = remoteQuery(() => value);

    await resource.refresh();
    value = 2;
    expect(resource.current).toBe(2);
    expect(await resource).toBe(2);

    await resource.refresh();
    value = 3;
    expect(resource.current).toBe(3);
  });

  it("is awaitable, as a query is in a template", async () => {
    const resource = remoteQuery(() => "ready");

    expect(await resource).toBe("ready");
  });
});
