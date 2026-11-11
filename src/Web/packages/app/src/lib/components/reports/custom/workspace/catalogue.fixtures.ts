import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import live from "./catalogue.fixture.json";

/** The catalogue a live API wrote, for a member who may read every dataset. */
// eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- a JSON import types its enum strings as string
export const liveCatalogue = live as unknown as CustomReportCatalogue;
