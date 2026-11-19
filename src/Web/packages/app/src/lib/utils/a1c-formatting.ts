import { a1cName, a1cUnits } from "#lib/stores/appearance-store.svelte.js";
import type { A1cDisplayValue } from "#lib/api/generated/nocturne-api-client.js";

export function a1cLabel(estimated = false): string {
  return `${estimated ? "e" : ""}${a1cName.current}`;
}

export function a1cUnitLabel(): string {
  return a1cUnits.current === "percent" ? "%" : "mmol/mol";
}

export function a1cDisplayValue<T>(
  value: { percent?: T; mmolMol?: T } | null | undefined
): T | undefined {
  return a1cUnits.current === "percent" ? value?.percent : value?.mmolMol;
}

export function formatA1cValue(
  value: A1cDisplayValue | null | undefined
): string {
  return formatA1cNumber(a1cDisplayValue(value));
}

/** One digit more than a lab report: two decimals for percent, one for mmol/mol. */
function a1cDecimals(): number {
  return a1cUnits.current === "percent" ? 2 : 1;
}

export function formatA1cNumber(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return "–";
  return value.toFixed(a1cDecimals());
}

export function formatA1c(value: A1cDisplayValue | null | undefined): string {
  const number = formatA1cValue(value);
  return number === "–"
    ? number
    : `${number}${a1cUnits.current === "percent" ? "" : " "}${a1cUnitLabel()}`;
}
