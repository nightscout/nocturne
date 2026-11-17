import { spawnSync } from "node:child_process";
import path from "node:path";

export const levels = ["suggestion", "warning", "error"] as const;
export type Level = (typeof levels)[number];

export interface ValeAlert {
  Check: string;
  Line: number;
  Match: string;
  Message: string;
  Severity: Level;
}

export class ValeError extends Error {}

export function runVale(
  repoRoot: string,
  target: string,
  minLevel: Level,
  command = "vale",
): Record<string, ValeAlert[]> {
  const vale = spawnSync(
    command,
    [
      `--config=${path.join(repoRoot, ".vale.ini")}`,
      `--minAlertLevel=${minLevel}`,
      "--output=JSON",
      target,
    ],
    { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, cwd: repoRoot },
  );
  if (vale.error) {
    throw new ValeError(
      "vale is not installed. See https://vale.sh/docs/install (winget install errata-ai.Vale).",
    );
  }
  if (vale.status !== 0 && vale.status !== 1) {
    throw new ValeError(vale.stderr || vale.stdout);
  }
  try {
    return JSON.parse(vale.stdout || "{}") as Record<string, ValeAlert[]>;
  } catch {
    throw new ValeError(`vale printed output that is not JSON: ${vale.stdout}`);
  }
}
