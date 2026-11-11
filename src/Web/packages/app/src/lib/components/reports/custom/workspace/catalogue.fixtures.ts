import type {
  CustomReportCatalogue,
  CustomReportDataset,
  CustomReportField,
} from "#lib/api/generated/nocturne-api-client.js";
import live from "./catalogue.fixture.json";

/**
 * The catalogue a live API wrote for a member who may read every dataset, trimmed to what the
 * workspace reads: no marks, ops or descriptions, and parameter schemas only for the event and
 * comparison datasets. Refresh it from `/api/v4/reports/custom/catalogue` when the engine's datasets
 * change; `catalogue-fixture.test.ts` fails when the generated client gains a member it lacks.
 */
// eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- a JSON import types its enum strings as string
export const liveCatalogue = live as unknown as CustomReportCatalogue;

/**
 * Every member of the catalogue's DTOs, each marked whether the generated client declares it
 * required. Typed against the client, so a member the API adds fails the type check here until it is
 * listed, and the fixture test then asks for it in the fixture.
 */
export const CATALOGUE_MEMBERS = {
  maxPanels: true,
  maxTracks: true,
  maxLayers: true,
  maxReads: true,
  maxTitleLength: true,
  maxRangeHours: true,
  maxNameLength: true,
  maxGridMinutes: true,
  panelKinds: true,
  marks: true,
  ops: true,
  datasets: true,
  workspace: true,
} satisfies Record<keyof CustomReportCatalogue, boolean>;

export const DATASET_MEMBERS = {
  name: true,
  label: true,
  description: true,
  requiredScope: true,
  permitted: true,
  parameters: true,
  fields: true,
  xKinds: true,
  panelKinds: true,
  marks: true,
  defaultRangeHours: true,
  maxRangeHours: true,
  rangeLimit: true,
  maxReadsPerReport: true,
  seriesField: false,
  seriesParameter: false,
  defaultSeriesCount: false,
} satisfies Record<keyof CustomReportDataset, boolean>;

export const FIELD_MEMBERS = {
  name: true,
  label: true,
  kind: true,
  unit: false,
  description: true,
  scope: false,
  permitted: true,
  aggregate: false,
  marker: false,
  setting: true,
} satisfies Record<keyof CustomReportField, boolean>;
