import { a1cName, a1cUnits } from "$lib/stores/appearance-store.svelte";
import type { A1cDisplayValue } from "$api/generated/nocturne-api-client";

export function a1cLabel(estimated = false): string {
  return `${estimated ? "e" : ""}${a1cName.current}`;
}

export function a1cUnitLabel(): string {
  return a1cUnits.current === "percent" ? "%" : "mmol/mol";
}

export function a1cDisplayValue(
  value: A1cDisplayValue | null | undefined
): number | undefined {
  return a1cUnits.current === "percent" ? value?.percent : value?.mmolMol;
}

export function formatA1cValue(
  value: A1cDisplayValue | null | undefined
): string {
  return formatA1cNumber(a1cDisplayValue(value));
}

export function formatA1cNumber(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return "–";
  return value.toFixed(a1cUnits.current === "percent" ? 1 : 0);
}

export function formatA1c(value: A1cDisplayValue | null | undefined): string {
  const number = formatA1cValue(value);
  return number === "–"
    ? number
    : `${number}${a1cUnits.current === "percent" ? "" : " "}${a1cUnitLabel()}`;
}
