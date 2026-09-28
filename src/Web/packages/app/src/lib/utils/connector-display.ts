import Activity from "@lucide/svelte/icons/activity";
import Cloud from "@lucide/svelte/icons/cloud";
import Database from "@lucide/svelte/icons/database";
import Plug from "@lucide/svelte/icons/plug";
import Settings from "@lucide/svelte/icons/settings";
import Smartphone from "@lucide/svelte/icons/smartphone";
import Sparkles from "@lucide/svelte/icons/sparkles";
import type { DataSourceStatus } from "$lib/components/settings/DataSourceRow.svelte";

/** Minimal connector status shape needed for display mapping */
interface ConnectorDisplayStatus {
  state?: string;
  isHealthy?: boolean;
}

export function getCategoryIcon(category: string | undefined) {
  switch (category) {
    case "cgm":
      return Activity;
    case "pump":
      return Database;
    case "aid-system":
      return Settings;
    case "connector":
      return Cloud;
    case "uploader":
      return Smartphone;
    case "demo":
      return Sparkles;
    default:
      return Plug;
  }
}

export function mapConnectorStatus(
  connectorStatus: ConnectorDisplayStatus
): DataSourceStatus {
  if (connectorStatus.state === "Syncing") return "syncing";
  if (connectorStatus.state === "BackingOff") return "backing-off";
  if (
    connectorStatus.state === "Error" ||
    (!connectorStatus.isHealthy && connectorStatus.state !== "Configured")
  )
    return "error";
  if (connectorStatus.state === "Configured") return "configured";
  if (connectorStatus.state === "Disabled") return "disabled";
  if (connectorStatus.state === "Offline") return "offline";
  return "active";
}
