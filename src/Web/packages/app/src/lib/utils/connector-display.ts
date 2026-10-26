import Activity from "@lucide/svelte/icons/activity";
import Cloud from "@lucide/svelte/icons/cloud";
import Database from "@lucide/svelte/icons/database";
import Plug from "@lucide/svelte/icons/plug";
import Settings from "@lucide/svelte/icons/settings";
import Smartphone from "@lucide/svelte/icons/smartphone";
import Sparkles from "@lucide/svelte/icons/sparkles";
import { ConnectorState } from "#lib/constants/connector-states.js";
import type { DataSourceStatus } from "#lib/components/settings/DataSourceRow.svelte";

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
  if (connectorStatus.state === ConnectorState.Syncing) return "syncing";
  if (connectorStatus.state === ConnectorState.BackingOff) return "backing-off";
  if (
    connectorStatus.state === ConnectorState.Error ||
    (!connectorStatus.isHealthy && connectorStatus.state !== ConnectorState.Configured)
  )
    return "error";
  if (connectorStatus.state === ConnectorState.Configured) return "configured";
  if (connectorStatus.state === ConnectorState.Disabled) return "disabled";
  if (connectorStatus.state === ConnectorState.Offline) return "offline";
  return "active";
}
