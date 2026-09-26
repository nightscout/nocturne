// The client loaders export only the runtime getters; key/loadCatalog/loadCount
// are server-side exports used to preload catalogs before runWithLocale.
export function getRuntime(loadID?: number): any;
export function getRuntimeRx(loadID?: number): any;
