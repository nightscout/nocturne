import { browser } from "$app/environment";
import { createAuthenticatedFetch } from "./auth-interceptor";
import { initialReadsStash, takeInitialRead } from "$lib/stores/initial-reads";
import {
  ApsSnapshotClient,
  BGCheckClient,
  BolusClient,
  ClockFacesClient,
  CurrentTherapyStateClient,
  DeviceEventClient,
  NoteClient,
  NotificationsClient,
  NutritionClient,
  ProfileClient,
  SensorGlucoseClient,
  TrackersClient,
  UISettingsClient,
} from "./generated/nocturne-api-client";

type Http = NonNullable<ConstructorParameters<typeof SensorGlucoseClient>[1]>;

let http: Http | null = null;

function browserHttp(): Http {
  if (!browser) {
    throw new Error(
      "Browser API clients should only be used in the browser. Use event.locals.apiClient in server-side code."
    );
  }
  http ??= {
    fetch: createAuthenticatedFetch((url, init) => {
      const prefetched = takeInitialRead(initialReadsStash(), url, init?.method);
      if (prefetched) return prefetched;
      return window.fetch(url, { ...init, credentials: "include" });
    }),
  };
  return http;
}

export const apsSnapshotClient = () => new ApsSnapshotClient("", browserHttp());
export const bgCheckClient = () => new BGCheckClient("", browserHttp());
export const bolusClient = () => new BolusClient("", browserHttp());
export const clockFacesClient = () => new ClockFacesClient("", browserHttp());
export const currentTherapyStateClient = () => new CurrentTherapyStateClient("", browserHttp());
export const deviceEventClient = () => new DeviceEventClient("", browserHttp());
export const noteClient = () => new NoteClient("", browserHttp());
export const notificationsClient = () => new NotificationsClient("", browserHttp());
export const nutritionClient = () => new NutritionClient("", browserHttp());
export const profileClient = () => new ProfileClient("", browserHttp());
export const sensorGlucoseClient = () => new SensorGlucoseClient("", browserHttp());
export const trackersClient = () => new TrackersClient("", browserHttp());
export const uiSettingsClient = () => new UISettingsClient("", browserHttp());

/** Drops the shared fetch so the next client is built on a fresh one. */
export function resetBrowserClients(): void {
  http = null;
}
