// Mirrors the Rust `CurrentBg` (camelCase) emitted by `get_current_glucose` / the
// `glucose-updated` event. `sgvMgdl`/`deltaMgdl` are mg/dL — display-unit conversion is the UI's job.
export type Reading = {
  sgvMgdl: number;
  deltaMgdl: number | null;
  direction: string | null;
  mills: number;
};

// Mirrors the Rust `ClockFaceSummary` returned by `list_clock_faces` — one of the linked user's
// clock faces for the floating-clock picker. `id` is the public-display capability the overlay opens.
export type ClockFaceOption = {
  id: string;
  name: string;
};
