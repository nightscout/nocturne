/**
 * The report pages a composition can mount, keyed by fixture and composition name. `page` is the
 * page's path under the reports route directory, when it is not `<name>/+page.svelte`.
 */
export const reports = {
  overview: { route: "/reports", page: "+page.svelte" },
  agp: { route: "/reports/agp" },
  "basal-analysis": { route: "/reports/basal-analysis" },
  battery: { route: "/reports/battery" },
  comparison: { route: "/reports/comparison" },
  "data-quality": { route: "/reports/data-quality" },
  "day-in-review": { route: "/reports/day-in-review" },
  ehba1c: { route: "/reports/ehba1c" },
  "executive-summary": { route: "/reports/executive-summary" },
  "glucose-distribution": { route: "/reports/glucose-distribution" },
  "heart-rate": { route: "/reports/heart-rate" },
  "hourly-stats": { route: "/reports/hourly-stats" },
  idp: { route: "/reports/idp" },
  "insulin-delivery": { route: "/reports/insulin-delivery" },
  readings: { route: "/reports/readings" },
  "site-change-impact": { route: "/reports/site-change-impact" },
  sleep: { route: "/reports/sleep" },
  steps: { route: "/reports/steps" },
  treatments: { route: "/reports/treatments" },
  "week-to-week": { route: "/reports/week-to-week" },
  "year-overview": { route: "/reports/year-overview" },
};
