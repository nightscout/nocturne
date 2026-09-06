import { vi } from "vitest";
import type {
  GoogleHealthPreview,
  GoogleHealthStatus,
  GoogleHealthReading,
} from "$lib/api";
import type { getGoogleHealthReadings as queryGoogleHealthReadings } from "$lib/api/generated/googleHealths.generated.remote";
import { effectAwareQuery } from "./effect-aware-query.svelte";

type ReadingsRequest = Parameters<typeof queryGoogleHealthReadings>[0];

export const googleHealthMocks = {
  status: vi.fn<() => Promise<GoogleHealthStatus>>(),
  readings:
    vi.fn<(request: ReadingsRequest) => Promise<GoogleHealthReading[]>>(),
  save: vi.fn(),
  start: vi.fn(),
  disconnect: vi.fn(),
  sync: vi.fn(),
  purge: vi.fn(),
  preview: vi.fn<() => Promise<GoogleHealthPreview>>(),
};

export const getGoogleHealth = () =>
  effectAwareQuery(googleHealthMocks.status);
export const getGoogleHealthReadings = (request: ReadingsRequest) =>
  effectAwareQuery(() => googleHealthMocks.readings(request));
export const saveGoogleHealth = googleHealthMocks.save;
export const startGoogleHealth = googleHealthMocks.start;
export const disconnectGoogleHealth = googleHealthMocks.disconnect;
export const syncGoogleHealth = googleHealthMocks.sync;
export const purgeGoogleHealth = googleHealthMocks.purge;
export const previewGoogleHealth = googleHealthMocks.preview;
