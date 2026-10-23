import { describe, it, expect } from "vitest";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import { Key } from "@nocturne/ui/keys";

/**
 * wuchale extracts a string literal that looks like a word, so `e.key === "Enter"` in a component
 * is translated and the handler stops matching in another locale. Key names come from `Key` in
 * `@nocturne/ui/keys`, a plain module wuchale never reads.
 *
 * This reads text and keys on the literal, not the receiver: any `<anything>.key` or bare `key`
 * compared with, `case`d on, or `includes`-tested against a DOM key name is an offence, so a
 * keyboard event under any name is seen and a StorageEvent key compared with a storage key is not.
 * It checks `.svelte` and `.svelte.{js,ts}` files, the ones wuchale extracts from.
 *
 * It does not see a key copied into another name first (`const k = e.key`), an array or set held
 * in a variable, or a key map with quoted key-name properties (`{ "Enter": go }[e.key]`), which
 * wuchale also extracts. Compare `e.key` with `Key` directly.
 */
const PACKAGES = fileURLToPath(new URL("../../../..", import.meta.url));

const NAMED_KEYS = String.raw`Arrow(?:Up|Down|Left|Right)|Page(?:Up|Down)|Home|End|Enter|Escape|Esc|Tab|Backspace|Delete|Insert|Shift|Control|Alt|Meta|F\d{1,2}|Spacebar`;
const escapeRegExp = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);
const KEY_NAME = [...Object.values(Key).map(escapeRegExp), NAMED_KEYS].join("|");
const KEY_LITERAL = String.raw`(["'\`])(?:${KEY_NAME})\1`;
const KEY_REF = String.raw`\bkey\b(?:\s*\.toLowerCase\(\))?`;

const COMPARES = [
  new RegExp(String.raw`${KEY_REF}\s*[!=]==?\s*${KEY_LITERAL}`, "g"),
  new RegExp(String.raw`${KEY_LITERAL}\s*[!=]==?\s*[\w.]*${KEY_REF}`, "g"),
  new RegExp(String.raw`\bcase\s+${KEY_LITERAL}\s*:`, "g"),
  new RegExp(String.raw`\[[^\]]*${KEY_LITERAL}[^\]]*\]\s*\.includes\(\s*[\w.]*${KEY_REF}\s*\)`, "g"),
];

function sourceFiles(dir: string): string[] {
  const files: string[] = [];

  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (["node_modules", ".svelte-kit", "dist", "build"].includes(entry.name)) continue;
    const path = join(dir, entry.name);
    if (entry.isDirectory()) files.push(...sourceFiles(path));
    else if (/\.svelte(\.(js|ts))?$/.test(entry.name) && !/\.test\./.test(entry.name)) files.push(path);
  }

  return files;
}

function offences(source: string): string[] {
  return COMPARES.flatMap((pattern) => [...source.matchAll(pattern)].map((match) => match[0].replace(/\s+/g, " ")));
}

describe("key names in wuchale-extracted files", () => {
  it("compares KeyboardEvent.key through Key, never a string literal", () => {
    const found = readdirSync(PACKAGES, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .filter((entry) => existsSync(join(PACKAGES, entry.name, "src")))
      .flatMap((entry) => sourceFiles(join(PACKAGES, entry.name, "src")))
      .flatMap((file) => offences(readFileSync(file, "utf8")).map((line) => `${file}: ${line}`));

    expect(found).toEqual([]);
  });

  it("recognises a literal comparison", () => {
    for (const line of [
      `if (e.key === "Enter") go();`,
      `if (e.key === ' ') go();`,
      `onkeydown={(event) => event.key !== "Escape" && stay()}`,
      `if ("Enter" === e.key) go();`,
      `if (props.event.key === 'Escape') close();`,
      `if (e.key.toLowerCase() === "p") print();`,
      `case "ArrowDown":`,
      `if (["Enter", " "].includes(e.key)) go();`,
      `if (keyEvent.key === "Enter") go();`,
      `onkeydown={({ key }) => key === "Escape" && close()}`,
      `if (e.key ===\n    "Enter") go();`,
      `if (["Home", "End"]\n  .includes(e.key)) go();`,
    ]) {
      expect(offences(line), line).toHaveLength(1);
    }
  });

  it("accepts Key and a non-keyboard key field", () => {
    for (const line of [
      `if (e.key === Key.Enter) go();`,
      `(c) => c.key === "notify"`,
      `const e = row.key;`,
      `if (e.key === "nocturne-color-theme") apply();`,
      `case "notify":`,
    ]) {
      expect(offences(line), line).toEqual([]);
    }
  });
});
