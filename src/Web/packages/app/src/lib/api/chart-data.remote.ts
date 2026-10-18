/**
 * Remote function for chart data with server-side calculations. All
 * categorization, color mapping, and treatment classification is done
 * server-side. Single call replaces 7+ separate API calls.
 */
import { query } from "$app/server";
import { z } from "zod";
import { getDashboardChartData } from "$api/generated/chartDatas.generated.remote";
import { transformChartData } from "$lib/utils/chart-data-transform";

const chartDataSchema = z.object({
  startTime: z.number(),
  endTime: z.number(),
  intervalMinutes: z.number().optional().default(5),
});

export const getChartData = query(
  chartDataSchema,
  async (input) =>
    transformChartData(await getDashboardChartData({ ...input, includeHealthSeries: false }))
);
