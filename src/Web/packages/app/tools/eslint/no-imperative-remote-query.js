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
 * A call made synchronously in the callback passed to `$effect`, `$effect.pre` or
 * `$derived.by`, before any `await` there, is in that reactive context, so `.then()`
 * on it only consumes the result and is not flagged.
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

const FUNCTION_TYPES = new Set([
  "FunctionExpression",
  "ArrowFunctionExpression",
  "FunctionDeclaration",
]);

function nearestFunction(node) {
  for (let n = node.parent; n; n = n.parent) {
    if (FUNCTION_TYPES.has(n.type)) return n;
  }
  return null;
}

function isReactiveCallback(fn) {
  const call = fn?.parent;
  if (!call || call.type !== "CallExpression" || call.arguments[0] !== fn) return false;
  const callee = call.callee;
  if (callee.type === "Identifier") return callee.name === "$effect";
  return (
    callee.type === "MemberExpression" &&
    callee.object.type === "Identifier" &&
    callee.property.type === "Identifier" &&
    ((callee.object.name === "$effect" && callee.property.name === "pre") ||
      (callee.object.name === "$derived" && callee.property.name === "by"))
  );
}

/**
 * @param {Set<string>} queryNames
 * @returns {import("eslint").Rule.RuleModule}
 */
export function createRule(queryNames) {
  return {
    meta: {
      type: "problem",
      docs: {
        description:
          "Remote query() functions called imperatively must use .run() (awaiting them outside a reactive context throws).",
      },
      schema: [],
      messages: {
        useRun:
          "Remote query '{{name}}' is called imperatively — use {{name}}(...).run() (or .current / .refresh()). Awaiting it directly throws outside a reactive context; defer .run() out of render via queueMicrotask if needed.",
      },
    },
    create(context) {
      // Only flag identifiers imported from a remote module in this file.
      const remoteQueryImports = new Set();
      // End offsets of the awaits made directly in each function, in source order.
      const awaitEnds = new Map();
      return {
        AwaitExpression(node) {
          const fn = nearestFunction(node);
          if (!fn) return;
          if (!awaitEnds.has(fn)) awaitEnds.set(fn, []);
          awaitEnds.get(fn).push(node.range[1]);
        },
        ImportDeclaration(node) {
          const source = String(node.source.value ?? "");
          if (!source.includes("remote")) return;
          for (const spec of node.specifiers) {
            if (spec.type !== "ImportSpecifier") continue;
            // The exported name is what the scan collected; the local name is what
            // the call sites use. `import { list as listGrants }` differs in both.
            const exported =
              spec.imported.type === "Identifier"
                ? spec.imported.name
                : String(spec.imported.value);
            if (queryNames.has(exported)) {
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

          if (!isAwaited && !isThenChain) return;

          const fn = nearestFunction(node);
          const afterAwait = (awaitEnds.get(fn) ?? []).some((end) => end <= node.range[0]);
          if (isReactiveCallback(fn) && !afterAwait) return;

          context.report({ node, messageId: "useRun", data: { name } });
        },
      };
    },
  };
}

export default createRule(loadQueryNames());
