import fs from "node:fs";
import path from "node:path";
import {
  runVale,
  ValeError,
  type Level,
  type ValeAlert,
} from "./valeRunner.ts";

export interface DocsFinding {
  rule: string;
  severity: Level;
  message: string;
  match: string;
  file: string;
  line: number;
}

export interface DocsResult {
  findings: DocsFinding[];
  error?: string;
}

export const frontmatterEnd = (lines: string[]): number => {
  if (lines[0]?.trimEnd() !== "---") return 0;
  const close = lines.findIndex((l, i) => i > 0 && l.trimEnd() === "---");
  return close + 1;
};

const holdsDocsPage = (dir: string): boolean =>
  fs.existsSync(dir) &&
  fs
    .readdirSync(dir, { recursive: true, encoding: "utf8" })
    .some((f) => f.endsWith(".svx"));

/**
 * Runs Vale over the portal docs pages with the `[*.svx]` section of
 * `.vale.ini`, which reads a page as Markdown and skips component tags. Vale
 * cannot skip frontmatter or a top-level import line, so findings on those
 * lines are dropped here. Each line is reported as it is in the file. A Vale
 * failure, or a directory that holds no docs page, comes back as `error` with
 * no findings: Vale reads a missing path as nothing to lint and exits 0.
 */
export function lintDocsProse(
  repoRoot: string,
  docsDir: string,
  minLevel: Level,
  command?: string,
): DocsResult {
  if (!holdsDocsPage(docsDir)) {
    return { findings: [], error: `No .svx page under ${docsDir}.` };
  }
  let report: Record<string, ValeAlert[]>;
  try {
    report = runVale(repoRoot, docsDir, minLevel, command);
  } catch (e) {
    if (!(e instanceof ValeError)) throw e;
    return { findings: [], error: e.message };
  }
  const findings = Object.entries(report).flatMap(([file, alerts]) => {
    const source = path.resolve(repoRoot, file);
    const lines = fs.readFileSync(source, "utf8").split(/\r?\n/);
    const skipUpTo = frontmatterEnd(lines);
    return alerts
      .filter(
        (a) =>
          a.Line > skipUpTo &&
          !/^\s*import\s.*\sfrom\s/.test(lines[a.Line - 1]),
      )
      .map((a) => ({
        rule: a.Check,
        severity: a.Severity,
        message: a.Message,
        match: a.Match,
        file: path.relative(repoRoot, source),
        line: a.Line,
      }));
  });
  return { findings };
}
