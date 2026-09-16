import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * A `catch` around a generated remote call that never reads the reason throws
 * it away and assigns fixed copy in its place, so someone is told "please try
 * again" about a duplicate name or a validation failure that retrying cannot
 * fix. `describeSubmitError` and `remoteErrorMessage` both take that copy as
 * their fallback, so converting a site never loses the wording it had.
 *
 * Binding nothing is one way; binding a name and never reading it is the same
 * loss, and the one a grep for `catch {` does not see. Both are asked the same
 * question here.
 *
 * Swallowing is sometimes right — a poll that runs again, an optimistic
 * rollback that reports itself by reappearing — and those say why in a comment
 * on the first line of the catch. That is all this asks for, and it is worth
 * knowing the limits:
 *
 * - It reads text, pairing a bare `catch` with the nearest `try` by brace depth,
 *   so a brace inside a string or comment can mispair it, and a `}` inside a
 *   string ends the catch body early.
 * - Whether a binding is read is a word match over that body text, so naming it
 *   in a string or a comment counts — `catch (e)` beside copy containing a
 *   standalone "e", or a `// TODO surface err`, passes without surfacing
 *   anything. A destructured binding, `catch ({ status })`, is not matched at
 *   all and so is never asked. Closing these needs an AST, which this is
 *   deliberately not.
 * - A comment satisfies it. It cannot tell a reason from an excuse; a site that
 *   keeps its fixed copy and adds a comment passes, and only review catches
 *   that.
 * - It sees calls to names imported from a `*.generated.remote` module, plus
 *   `.run()` and `.refresh()`. A remote call reached any other way is
 *   invisible.
 */
const SRC = fileURLToPath(new URL("../..", import.meta.url));

/** Names imported from a generated remote module, which a `try` may be awaiting. */
function remoteImports(source: string): string[] {
  const names = new Set<string>();

  for (const match of source.matchAll(
    /import\s*(?:\*\s*as\s*(\w+)|\{([^}]*)\})\s*from\s*["'][^"']*generated\.remote[^"']*["']/g
  )) {
    const [, namespace, clause] = match;
    if (namespace) {
      names.add(namespace);
      continue;
    }
    for (const entry of clause.split(",")) {
      const name = entry
        .trim()
        .split(/\s+as\s+/)
        .pop()
        ?.trim();
      if (name) names.add(name);
    }
  }

  return [...names];
}

/** The body of the `try` a bare `catch` at `catchIndex` belongs to. */
function tryBlockBefore(source: string, catchIndex: number): string {
  let depth = 0;

  for (let i = catchIndex - 1; i >= 0; i--) {
    const char = source[i];
    if (char === "}") depth++;
    else if (char === "{") {
      if (depth === 0) return source.slice(i, catchIndex);
      depth--;
    }
  }

  return source.slice(0, catchIndex);
}

function callsRemote(block: string, imported: string[]): boolean {
  if (/\.(run|refresh)\s*\(/.test(block)) return true;
  return imported.some((name) => new RegExp(`\\b${name}\\s*[(.]`).test(block));
}

/** The catch body at `catchIndex`, without its braces. */
function catchBody(source: string, catchIndex: number): string {
  const open = source.indexOf("{", catchIndex);
  let depth = 0;

  for (let i = open; i < source.length; i++) {
    const char = source[i];
    if (char === "{") depth++;
    else if (char === "}") {
      depth--;
      if (depth === 0) return source.slice(open + 1, i);
    }
  }

  return source.slice(open + 1);
}

/** Whether the catch body opens with a comment explaining the silence. */
function explainsItself(source: string, catchIndex: number): boolean {
  return /^\s*(\/\/|\/\*)/.test(catchBody(source, catchIndex));
}

/**
 * A binding the body never reads discards the reason exactly as a bare `catch`
 * does, and costs a grep for `catch {` nothing to miss. Both forms are held to
 * the same rule below.
 */
function readsBinding(
  source: string,
  catchIndex: number,
  binding: string
): boolean {
  return new RegExp(`\\b${binding}\\b`).test(catchBody(source, catchIndex));
}

const CATCH = /\}\s*catch\s*(?:\(\s*(\w+)[^)]*\)\s*)?\{/g;

interface Offence {
  file: string;
  line: number;
}

interface SourceFile {
  file: string;
  source: string;
}

let cachedSources: SourceFile[] | undefined;

/**
 * Every source file under `src/` with its text. Walking the tree and reading
 * ~800 files synchronously is the entire cost of this file — the pattern
 * matching below is milliseconds — so both guards share one pass rather than
 * each taking its own.
 */
function sources(): SourceFile[] {
  // readdirSync yields the platform's separator, so the directory exclusions
  // below would only bite on posix if the paths were left as they arrive.
  cachedSources ??= readdirSync(SRC, { recursive: true, encoding: "utf8" })
    .map((file) => file.replaceAll("\\", "/"))
    .filter(
      (file) =>
        /\.(svelte|ts)$/.test(file) &&
        !file.endsWith(".test.ts") &&
        !/(^|\/)(generated|test-stubs)(\/|$)/.test(file)
    )
    .map((file) => ({ file, source: readFileSync(`${SRC}/${file}`, "utf8") }));

  return cachedSources;
}

/**
 * Budget for a guard that reads the whole source tree. The default five seconds
 * is sized for logic tests; a cold file cache alone can put these reads an order
 * of magnitude above it.
 */
const WALK_TIMEOUT_MS = 60_000;

function offences(): { found: Offence[]; scanned: number } {
  const found: Offence[] = [];
  let scanned = 0;

  for (const { file, source } of sources()) {
    const imported = remoteImports(source);
    if (imported.length === 0) continue;
    scanned++;

    for (const match of source.matchAll(CATCH)) {
      const index = match.index!;
      const binding = match[1];
      if (binding && readsBinding(source, index, binding)) continue;
      if (!callsRemote(tryBlockBefore(source, index), imported)) continue;
      if (explainsItself(source, index)) continue;

      found.push({
        file,
        line: source.slice(0, index).split("\n").length,
      });
    }
  }

  return { found, scanned };
}

/**
 * The other way a site loses the copy it chose: `errorMessage` is the raw
 * accessor for `body.message`, which reports what NSwag, SvelteKit or our own
 * codegen synthesized as readily as what the server wrote. A `??` on it
 * therefore renders "An unexpected server error occurred." exactly where the
 * fallback beside it was meant to stand in. `describeSubmitError` and
 * `remoteErrorMessage` take that same sentence as their fallback and consult
 * `CLIENT_WRITTEN_REASONS` first.
 *
 * Reading the value for something other than display — matching it against a
 * closed set of failure codes, as `totp-errors` does — is not this shape and is
 * not asked about.
 *
 * Limits: the argument may nest one level of parentheses; a value bound on one
 * line and defaulted on the next, or defaulted through a ternary, is not seen.
 */
const RAW_FALLBACK = /\berrorMessage\((?:[^()]|\([^()]*\))*\)\s*(?:\?\?|\|\|)/g;

function rawFallbacks(): { found: Offence[]; scanned: number } {
  const found: Offence[] = [];
  const files = sources();

  for (const { file, source } of files) {
    for (const match of source.matchAll(RAW_FALLBACK)) {
      found.push({
        file,
        line: source.slice(0, match.index!).split("\n").length,
      });
    }
  }

  return { found, scanned: files.length };
}

describe("fallbacks beside a remote call's reason", () => {
  it(
    "are reached through a helper that knows who wrote it",
    () => {
      const { found, scanned } = rawFallbacks();

      expect(scanned).toBeGreaterThan(400);
      expect(found).toEqual([]);
    },
    WALK_TIMEOUT_MS
  );

  it("still recognises a raw fallback when it sees one", () => {
    const lossy = `oidcError = errorMessage(err) ?? "Failed to delete provider.";`;

    expect([...lossy.matchAll(RAW_FALLBACK)]).toHaveLength(1);
  });

  it("leaves a read that is not a fallback alone", () => {
    const matching = `const body = errorMessage(err)?.trim();`;
    const wrapped = `return remoteErrorMessage(err, "Failed to load.");`;

    expect([...matching.matchAll(RAW_FALLBACK)]).toHaveLength(0);
    expect([...wrapped.matchAll(RAW_FALLBACK)]).toHaveLength(0);
  });
});

describe("catches around generated remote calls", () => {
  it(
    "bind the error, or say why they do not",
    () => {
      const { found, scanned } = offences();

      // An empty result is the pass condition, so a walk that read nothing — a
      // moved source root, a separator the filter did not expect — would pass
      // for the wrong reason. Assert it found files to judge.
      expect(scanned).toBeGreaterThan(20);
      expect(found).toEqual([]);
    },
    WALK_TIMEOUT_MS
  );

  it("still recognises a discarded reason when it sees one", () => {
    // The guard is only worth its cost if it fails on the shape it exists to
    // catch, so exercise its parts on that shape directly.
    const lossy = `
      import { createRole } from "$api/generated/roles.generated.remote";
      async function handle() {
        try {
          await createRole({ name });
        } catch {
          errorMessage = "Failed to create role. Please try again.";
        }
      }
    `;

    const index = lossy.indexOf("} catch {");
    expect(remoteImports(lossy)).toContain("createRole");
    expect(callsRemote(tryBlockBefore(lossy, index), ["createRole"])).toBe(
      true
    );
    expect(explainsItself(lossy, index)).toBe(false);
  });

  it("recognises a bound reason the body never reads", () => {
    const lossy = `
      import { revoke } from "$api/generated/sessions.generated.remote";
      async function handle() {
        try {
          await revoke(id);
        } catch (err) {
          errorMessage = "Failed to sign out the session. Please try again.";
        }
      }
    `;

    const index = lossy.indexOf("} catch (err) {");
    const [match] = [...lossy.matchAll(CATCH)];

    expect(match[1]).toBe("err");
    expect(readsBinding(lossy, index, "err")).toBe(false);
    expect(callsRemote(tryBlockBefore(lossy, index), ["revoke"])).toBe(true);
  });

  it("leaves a bound reason the body reads alone", () => {
    const routed = `
      import { revoke } from "$api/generated/sessions.generated.remote";
      async function handle() {
        try {
          await revoke(id);
        } catch (err) {
          errorMessage = describeSubmitError(err, "Failed to sign out.");
        }
      }
    `;

    const index = routed.indexOf("} catch (err) {");
    expect(readsBinding(routed, index, "err")).toBe(true);
  });

  it("passes a swallow that explains itself", () => {
    const deliberate = `
      import { getStatus } from "$api/generated/jobs.generated.remote";
      async function poll() {
        try {
          await getStatus().refresh();
        } catch {
          // One failed poll says nothing; the next one runs in two seconds.
        }
      }
    `;

    const index = deliberate.indexOf("} catch {");
    expect(explainsItself(deliberate, index)).toBe(true);
  });

  it("ignores a catch that has nothing to do with a remote call", () => {
    const local = `
      import { getStatus } from "$api/generated/jobs.generated.remote";
      function parse(raw: string) {
        try {
          return JSON.parse(raw);
        } catch {
          return null;
        }
      }
    `;

    const index = local.indexOf("} catch {");
    expect(callsRemote(tryBlockBefore(local, index), ["getStatus"])).toBe(
      false
    );
  });
});
