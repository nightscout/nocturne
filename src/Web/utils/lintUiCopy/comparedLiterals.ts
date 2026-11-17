import fs from "node:fs";
import path from "node:path";

export interface CatalogEntry {
  msgid: string;
  references: string[];
}

export interface ComparedLiteral {
  literal: string;
  line: number;
}

export interface ComparisonFinding extends ComparedLiteral {
  file: string;
}

const quoted = (q: string) => `${q}((?:[^${q}\\\\\\n]|\\\\.)*)${q}`;
const literal = `(?:${quoted('"')}|${quoted("'")}|${quoted("`")})`;
const operator = "(?:===|!==|==|!=)";
const comparisons = [
  new RegExp(`${operator}\\s*${literal}`, "g"),
  new RegExp(`${literal}\\s*${operator}`, "g"),
  new RegExp(`\\bcase\\s+${literal}\\s*:`, "g"),
  new RegExp(`\\.(?:includes|startsWith|endsWith)\\(\\s*${literal}`, "g"),
];

/** Every string literal the source compares with an equality operator, a `case` label or a string search call. */
export function findComparedLiterals(source: string): ComparedLiteral[] {
  const found: ComparedLiteral[] = [];
  for (const pattern of comparisons) {
    for (const match of source.matchAll(pattern)) {
      const text = (match[1] ?? match[2] ?? match[3]).replace(/\\(.)/g, "$1");
      const line = source.slice(0, match.index).split("\n").length;
      found.push({ literal: text, line });
    }
  }
  return found;
}

/**
 * A catalog string that its own referencing file compares against. In a
 * non-English locale the comparison tests the translation, so it never matches.
 */
export function findTranslatedComparisons(
  entries: CatalogEntry[],
  baseDir: string,
): Map<string, ComparisonFinding[]> {
  const msgidsByFile = new Map<string, Set<string>>();
  for (const entry of entries) {
    for (const reference of entry.references) {
      const file = reference.replace(/:\d+$/, "");
      if (!msgidsByFile.has(file)) msgidsByFile.set(file, new Set());
      msgidsByFile.get(file)!.add(entry.msgid);
    }
  }

  const findings = new Map<string, ComparisonFinding[]>();
  for (const [file, msgids] of msgidsByFile) {
    const full = path.resolve(baseDir, file);
    if (!fs.existsSync(full)) continue;
    for (const compared of findComparedLiterals(fs.readFileSync(full, "utf8"))) {
      if (!msgids.has(compared.literal)) continue;
      const list = findings.get(file) ?? [];
      list.push({ ...compared, file });
      findings.set(file, list);
    }
  }
  return findings;
}
