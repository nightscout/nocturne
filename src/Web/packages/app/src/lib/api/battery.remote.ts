import { getRequestEvent, query } from "$app/server";
import { z } from "zod";

const currentBatterySchema = z.object({
  recentMinutes: z.number().optional().default(30),
});

const batteryReportSchema = z.object({
  device: z.string().optional().nullable(),
  from: z.number(),
  to: z.number(),
  cycleLimit: z.number().optional().default(50),
});

/** Get all data needed for the battery status card */
export const getBatteryCardData = query(currentBatterySchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const [currentStatus, statistics] = await Promise.all([
    apiClient.battery.getCurrentBatteryStatus(props.recentMinutes),
    apiClient.battery.getBatteryStatistics(),
  ]);

  return { currentStatus, statistics };
});

/** Get all data needed for the battery report page */
export const getBatteryReportData = query(
  batteryReportSchema,
  async (props) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const fromDate = new Date(props.from);
    const toDate = new Date(props.to);

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
