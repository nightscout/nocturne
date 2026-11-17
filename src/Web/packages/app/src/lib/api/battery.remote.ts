import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import { DateRangeSchema, resolveReportRange } from "#lib/api/report-range.js";

const batteryReportSchema = DateRangeSchema.extend({
  device: z.string().optional().nullable(),
  cycleLimit: z.number().optional().default(50),
});

/** Get all data needed for the battery report page */
export const getBatteryReportData = query(
  batteryReportSchema,
  async (props) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const { startDate: fromDate, endDate: toDate } =
      await resolveReportRange(props);

    const [statistics, cycles, readings] = await Promise.all([
      apiClient.battery.getBatteryStatistics(
        props.device ?? undefined,
        fromDate,
        toDate
      ),
      apiClient.battery.getChargeCycles(
        props.device ?? undefined,
        fromDate,
        toDate,
        props.cycleLimit
      ),
      apiClient.battery.getBatteryReadings(
        props.device ?? undefined,
        fromDate,
        toDate
      ),
    ]);

    return {
      statistics: statistics ?? [],
      cycles: cycles ?? [],
      readings: readings ?? [],
    };
  }
);
