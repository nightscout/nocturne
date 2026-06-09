import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Custom ESLint rule: remote `query()` functions called imperatively must use
 * `.run()` (or `.current` / `.refresh()`).
 *
 * SvelteKit remote `query()` functions can be awaited directly only when
 * created in a reactive context (component init, `$effect`, `$derived`,
 * `{#await}`). When created in a detached continuation — after an `await`,
 * inside `setInterval` / `setTimeout` / observer callbacks, or via `.then()` —
 * awaiting them throws "not created in a reactive context ... Use .run()". This
 * rule flags those imperative call sites. Mutations use `command()`, which is
 * correctly awaited directly, so only `query()` exports are matched.
 *
 * The set of query names is discovered by scanning the generated + hand-written
 * remote modules under `src/lib/api`. Any failure to scan yields an empty set,
 * which silently disables the rule rather than crashing the lint run.
 */

const ruleDir = path.dirname(fileURLToPath(import.meta.url));
const SAFE_MEMBERS = new Set(["run", "current", "refresh"]);

function loadQueryNames() {
  const names = new Set();
  const root = path.resolve(ruleDir, "..", "..", "src", "lib", "api");
  const stack = [root];
  try {
    while (stack.length > 0) {
      const dir = stack.pop();
      // eslint-disable-next-line security/detect-non-literal-fs-filename
      const entries = fs.readdirSync(dir, { withFileTypes: true });
      for (const entry of entries) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
          stack.push(full);
        } else if (entry.name.endsWith(".remote.ts")) {
          // eslint-disable-next-line security/detect-non-literal-fs-filename
          const src = fs.readFileSync(full, "utf8");
          const re = /export\s+const\s+(\w+)\s*=\s*query\(/g;
          let match;
          while ((match = re.exec(src)) !== null) {
            names.add(match[1]);
          }
        }
      }
    }
  } catch {
    // Scan failed (path moved, permissions, etc.) — disable the rule rather
    // than break linting.
    return new Set();
  }
  return names;
}

const QUERY_NAMES = loadQueryNames();

/** @type {import("eslint").Rule.RuleModule} */
const rule = {
  meta: {
    type: "problem",
    docs: {
      description:
        "Remote query() functions called imperatively must use .run() (awaiting them outside a reactive context throws).",
    },
    schema: [],
    messages: {
      useRun:
        "Remote query '{{name}}' is called imperatively — use {{name}}(...).run() (or .current / .refresh()). Awaiting it directly throws outside a reactive context; defer .run() out of render via queueMicrotask if needed (see HaloDial.svelte).",
    },
  },
  create(context) {
    // Only flag identifiers imported from a remote module in this file.
    const remoteQueryImports = new Set();
    return {
      ImportDeclaration(node) {
        const source = String(node.source.value ?? "");
        if (!source.includes("remote")) return;
        for (const spec of node.specifiers) {
          if (
            spec.type === "ImportSpecifier" &&
            QUERY_NAMES.has(spec.local.name)
          ) {
            remoteQueryImports.add(spec.local.name);
          }
        }
      },
      CallExpression(node) {
        if (node.callee.type !== "Identifier") return;
        const name = node.callee.name;
        if (!remoteQueryImports.has(name)) return;

        const parent = node.parent;
        // `getX(...).run()` / `.current` / `.refresh()` — the correct imperative form.
        if (
          parent &&
          parent.type === "MemberExpression" &&
          parent.object === node &&
          parent.property.type === "Identifier" &&
          SAFE_MEMBERS.has(parent.property.name)
        ) {
          return;
        }

        const isAwaited = parent && parent.type === "AwaitExpression";
        const isThenChain =
          parent &&
          parent.type === "MemberExpression" &&
          parent.object === node &&
          parent.property.type === "Identifier" &&
          (parent.property.name === "then" || parent.property.name === "catch");

        if (isAwaited || isThenChain) {
          context.report({ node, messageId: "useRun", data: { name } });
        }
      },
    };
  },
};

export default rule;
