import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * A `catch` binding nothing around a generated remote call throws the server's
 * reason away and assigns fixed copy in its place, so someone is told "please
 * try again" about a duplicate name or a validation failure that retrying
 * cannot fix. `describeSubmitError` and `remoteErrorMessage` both take that
 * copy as their fallback, so converting a site never loses the wording it had.
 *
 * Swallowing is sometimes right — a poll that runs again, an optimistic
 * rollback that reports itself by reappearing — and those say why in a comment
 * on the first line of the catch. That is all this asks for, and it is worth
 * knowing the limits:
 *
 * - It reads text, pairing a bare `catch` with the nearest `try` by brace depth,
 *   so a brace inside a string or comment can mispair it.
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

/** Whether the catch body opens with a comment explaining the silence. */
function explainsItself(source: string, catchIndex: number): boolean {
  const body = source.slice(source.indexOf("{", catchIndex) + 1);
  return /^\s*(\/\/|\/\*)/.test(body);
}

interface Offence {
  file: string;
  line: number;
}

function sourceFiles(): string[] {
  // readdirSync yields the platform's separator, so the directory exclusions
  // below would only bite on posix if the paths were left as they arrive.
  return readdirSync(SRC, { recursive: true, encoding: "utf8" })
    .map((file) => file.replaceAll("\\", "/"))
    .filter(
      (file) =>
        /\.(svelte|ts)$/.test(file) &&
        !file.endsWith(".test.ts") &&
        !/(^|\/)(generated|test-stubs)(\/|$)/.test(file)
    );
}

function offences(): { found: Offence[]; scanned: number } {
  const found: Offence[] = [];
  let scanned = 0;

  for (const file of sourceFiles()) {
    const source = readFileSync(`${SRC}/${file}`, "utf8");
    const imported = remoteImports(source);
    if (imported.length === 0) continue;
    scanned++;

    for (const match of source.matchAll(/\}\s*catch\s*\{/g)) {
      const index = match.index!;
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

describe("catches around generated remote calls", () => {
  it("bind the error, or say why they do not", () => {
    const { found, scanned } = offences();

    // An empty result is the pass condition, so a walk that read nothing — a
    // moved source root, a separator the filter did not expect — would pass
    // for the wrong reason. Assert it found files to judge.
    expect(scanned).toBeGreaterThan(20);
    expect(found).toEqual([]);
  });

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
