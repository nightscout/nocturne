import { getContext, setContext } from "svelte";
import type { CurrentBatteryStatus, MultiPeriodStatistics } from "$lib/api";

// @wc-ignore
const key = "nocturne:app:dashboard-examples";

export interface DashboardExamples {
  statistics: MultiPeriodStatistics;
  battery: CurrentBatteryStatus;
  now: number;
  connected: boolean;
}

export function provideDashboardExamples(examples: DashboardExamples) {
  setContext(key, examples);
}

export function dashboardExamples(): DashboardExamples | undefined {
  return getContext(key);
}
