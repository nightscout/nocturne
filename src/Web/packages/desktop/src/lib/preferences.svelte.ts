import type { GlucoseUnits } from "@nocturne/ui/glucose";

// Persisted in the webview's localStorage, shared across the companion's windows (same origin) —
// so the floating clock window and the settings page read and write the same values. The companion
// has no server-side profile and the reading file is canonical mg/dL, so these are purely local
// choices. Units default to mmol, matching Nocturne's own default display unit.

const UNITS_KEY = "nocturne-companion-units";
const CLOCK_ID_KEY = "nocturne-companion-clock-id";
const FLOAT_OPACITY_KEY = "nocturne-companion-float-opacity";
const FLOAT_AOT_KEY = "nocturne-companion-float-always-on-top";

function read(key: string): string | null {
  return typeof localStorage === "undefined" ? null : localStorage.getItem(key);
}

function write(key: string, value: string): void {
  if (typeof localStorage !== "undefined") localStorage.setItem(key, value);
}

function loadUnits(): GlucoseUnits {
  const stored = read(UNITS_KEY);
  return stored === "mg/dl" || stored === "mmol" ? stored : "mmol";
}

function loadFloatOpacity(): number {
  const n = Number(read(FLOAT_OPACITY_KEY));
  return Number.isFinite(n) && n >= 0.3 && n <= 1 ? n : 1;
}

let units = $state<GlucoseUnits>(loadUnits());
let clockId = $state<string>(read(CLOCK_ID_KEY) ?? "");
let floatOpacity = $state<number>(loadFloatOpacity());
// Default the overlay to always-on-top — that's the point of a floating widget.
let floatAlwaysOnTop = $state<boolean>(read(FLOAT_AOT_KEY) !== "false");

export const preferences = {
  get units(): GlucoseUnits {
    return units;
  },
  set units(value: GlucoseUnits) {
    units = value;
    write(UNITS_KEY, value);
  },
  // The clock face the floating window shows (its UUID, the public-display capability).
  get clockId(): string {
    return clockId;
  },
  set clockId(value: string) {
    clockId = value;
    write(CLOCK_ID_KEY, value);
  },
  get floatOpacity(): number {
    return floatOpacity;
  },
  set floatOpacity(value: number) {
    floatOpacity = value;
    write(FLOAT_OPACITY_KEY, String(value));
  },
  get floatAlwaysOnTop(): boolean {
    return floatAlwaysOnTop;
  },
  set floatAlwaysOnTop(value: boolean) {
    floatAlwaysOnTop = value;
    write(FLOAT_AOT_KEY, String(value));
  },
};
