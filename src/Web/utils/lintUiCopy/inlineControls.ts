import fs from "node:fs";
import path from "node:path";

export type ControlProblem =
  | "no reason"
  | "never restarted"
  | "not on its own line";

export interface ControlFinding {
  file: string;
  line: number;
  text: string;
  problem: ControlProblem;
}

const minReasonWords = 3;

/**
 * A control that stops linting is matched loosely: a looser match can only
 * report more. `off` stops everything, `<target> = NO` stops one rule or style.
 */
const stopPattern = /^\s*vale\s+(?:(off)|([^\s=]+)\s*=\s*no)\s*$/i;

/**
 * A control that starts linting again is matched in the one form Vale 3.24
 * honours: `vale on`, or `<target> = YES` with the target spelt as the stop
 * spells it, and single spaces. Vale ignores `= Yes`, `=YES`, a lowercase
 * target and `vale  on`, and the page stays unlinted to its end.
 */
const startPattern = /^<!--\s*vale (?:(on)|([^\s=]+) = YES)\s*-->$/;

const comment = /^\s*<!--([\s\S]*?)-->\s*$/;
const anyControl = /^\s*<!--\s*vale\s/i;

interface Control {
  line: number;
  text: string;
  stops: boolean;
  target: string;
  alone: boolean;
}

const isReason = (line: string | undefined): boolean => {
  if (line === undefined || anyControl.test(line)) return false;
  const text = comment.exec(line)?.[1].trim() ?? "";
  return text.split(/\s+/).filter(Boolean).length >= minReasonWords;
};

/** Every HTML comment of the page that Vale reads as a control, in order. */
function controlsOf(source: string): Control[] {
  return [...source.matchAll(/<!--[\s\S]*?-->/g)].flatMap((match) => {
    const text = match[0];
    const stop = stopPattern.exec(text.slice(4, -3));
    const start = stop ? null : startPattern.exec(text);
    if (!stop && !start) return [];
    const index = match.index;
    const lineStart = source.lastIndexOf("\n", index - 1) + 1;
    const newline = source.indexOf("\n", index + text.length);
    const lineEnd = newline === -1 ? source.length : newline;
    return [
      {
        line: source.slice(0, index).split("\n").length,
        text: text.replace(/\s+/g, " "),
        stops: stop !== null,
        target: stop ? (stop[1] ? "*" : stop[2]) : start![1] ? "*" : start![2],
        alone:
          !text.includes("\n") &&
          source.slice(lineStart, index).trim() === "" &&
          source.slice(index + text.length, lineEnd).trim() === "",
      },
    ];
  });
}

/**
 * Problems with the Vale controls of a page. A control must stand alone on its
 * line, where a reader and this check can see it. One that stops linting needs
 * a reason (an HTML comment of at least three words on the line before or
 * after it) and a later control that starts it again for the same target.
 * Without one, Vale skips the rest of the page.
 */
export function controlProblems(
  source: string,
): Pick<ControlFinding, "line" | "text" | "problem">[] {
  const lines = source.split(/\r?\n/);
  const controls = controlsOf(source);
  return controls.flatMap((control, i) => {
    const { line, text } = control;
    if (!control.alone) {
      return [{ line, text, problem: "not on its own line" as const }];
    }
    if (!control.stops) return [];
    const problems: ControlProblem[] = [];
    if (!isReason(lines[line - 2]) && !isReason(lines[line])) {
      problems.push("no reason");
    }
    const restarted = controls
      .slice(i + 1)
      .some((c) => c.alone && !c.stops && c.target === control.target);
    if (!restarted) problems.push("never restarted");
    return problems.map((problem) => ({ line, text, problem }));
  });
}

export function findControlProblems(
  repoRoot: string,
  docsDir: string,
): ControlFinding[] {
  if (!fs.existsSync(docsDir)) return [];
  return fs
    .readdirSync(docsDir, { recursive: true, encoding: "utf8" })
    .filter((f) => f.endsWith(".svx"))
    .sort()
    .flatMap((name) => {
      const file = path.join(docsDir, name);
      return controlProblems(fs.readFileSync(file, "utf8")).map((p) => ({
        ...p,
        file: path.relative(repoRoot, file),
      }));
    });
}
