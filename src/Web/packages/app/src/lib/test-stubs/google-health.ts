import { vi } from "vitest";
import type {
  GoogleHealthPreview,
  GoogleHealthStatus,
} from "$lib/api";
import { effectAwareQuery } from "./effect-aware-query.svelte";

export const googleHealthMocks = {
  status: vi.fn<() => Promise<GoogleHealthStatus>>(),
  save: vi.fn(),
  start: vi.fn(),
  disconnect: vi.fn(),
  sync: vi.fn(),
  purge: vi.fn(),
  preview: vi.fn<() => Promise<GoogleHealthPreview>>(),
};

export const getGoogleHealth = () =>
  effectAwareQuery(googleHealthMocks.status);
export const saveGoogleHealth = googleHealthMocks.save;
export const startGoogleHealth = googleHealthMocks.start;
export const disconnectGoogleHealth = googleHealthMocks.disconnect;
export const syncGoogleHealth = googleHealthMocks.sync;
export const purgeGoogleHealth = googleHealthMocks.purge;
export const previewGoogleHealth = googleHealthMocks.preview;
export const getPatientRecord = () => ({ current: null });
export const getRealtimeStore = () => ({ syncProgressByConnector: {} });
