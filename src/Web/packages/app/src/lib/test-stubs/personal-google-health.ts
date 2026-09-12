import { vi } from "vitest";
import type {
  GoogleHealthPreview,
  GoogleHealthStatus,
  PersonalHealthReading,
} from "$lib/api";
import type { getPersonalHealthReadings as queryPersonalHealthReadings } from "$lib/api/generated/personalGoogleHealths.generated.remote";
import { effectAwareQuery } from "./effect-aware-query.svelte";

type ReadingsRequest = Parameters<typeof queryPersonalHealthReadings>[0];

export const googleHealthMocks = {
  status: vi.fn<() => Promise<GoogleHealthStatus>>(),
  readings:
    vi.fn<(request: ReadingsRequest) => Promise<PersonalHealthReading[]>>(),
  save: vi.fn(),
  start: vi.fn(),
  disconnect: vi.fn(),
  sync: vi.fn(),
  purge: vi.fn(),
  preview: vi.fn<() => Promise<GoogleHealthPreview>>(),
};

export const getPersonalGoogleHealth = () =>
  effectAwareQuery(googleHealthMocks.status);
export const getPersonalHealthReadings = (request: ReadingsRequest) =>
  effectAwareQuery(() => googleHealthMocks.readings(request));
export const savePersonalGoogleHealth = googleHealthMocks.save;
export const startPersonalGoogleHealth = googleHealthMocks.start;
export const disconnectPersonalGoogleHealth = googleHealthMocks.disconnect;
export const syncPersonalGoogleHealth = googleHealthMocks.sync;
export const purgePersonalGoogleHealth = googleHealthMocks.purge;
export const previewPersonalGoogleHealth = googleHealthMocks.preview;
