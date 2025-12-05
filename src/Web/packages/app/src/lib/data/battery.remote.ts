import { getRequestEvent, query } from "$app/server";
import type { BatteryStatistics, CurrentBatteryStatus } from "$lib/api";
import { z } from "zod";

const currentBatterySchema = z.object({
  recentMinutes: z.number().optional().default(30),
});

const batteryStatsSchema = z.object({
  device: z.string().optional(),
  from: z.number().optional(),
  to: z.number().optional(),
});

const batteryReportSchema = z.object({
  device: z.string().optional().nullable(),
  from: z.number(),
  to: z.number(),
  cycleLimit: z.number().optional().default(50),
});

/**
 * Get the current battery status for all tracked devices
 */
export const getCurrentBatteryStatus = query(currentBatterySchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const status: CurrentBatteryStatus = await apiClient.battery.getCurrentBatteryStatus(
    props.recentMinutes
  );
  return status;
});

/**
 * Get battery statistics for a device or all devices
 */
export const getBatteryStatistics = query(batteryStatsSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const statistics: BatteryStatistics[] = await apiClient.battery.getBatteryStatistics(
    props.device,
    props.from,
    props.to
  );
  return statistics;
});

/**
 * Get all data needed for the battery status card
 */
export const getBatteryCardData = query(currentBatterySchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const [currentStatus, statistics] = await Promise.all([
    apiClient.battery.getCurrentBatteryStatus(props.recentMinutes),
    apiClient.battery.getBatteryStatistics(),
  ]);

  return { currentStatus, statistics };
});

/**
 * Get all data needed for the battery report page
 */
export const getBatteryReportData = query(batteryReportSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const [statistics, cycles, readings] = await Promise.all([
    apiClient.battery.getBatteryStatistics(
      props.device ?? undefined,
      props.from,
      props.to
    ),
    apiClient.battery.getChargeCycles(
      props.device ?? undefined,
      props.from,
      props.to,
      props.cycleLimit
    ),
    apiClient.battery.getBatteryReadings(
      props.device ?? undefined,
      props.from,
      props.to
    ),
  ]);

  return {
    statistics: statistics ?? [],
    cycles: cycles ?? [],
    readings: readings ?? [],
  };
});
