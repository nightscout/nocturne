import type { LoaderFunc } from 'wuchale/load-utils';
export const key: string;
export const loadCatalog: LoaderFunc;
export const loadCount: number;
export function getRuntime(loadID?: number): any;
export function getRuntimeRx(loadID?: number): any;
