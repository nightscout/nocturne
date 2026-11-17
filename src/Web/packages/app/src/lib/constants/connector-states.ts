export const ConnectorState = {
  Syncing: "Syncing",
  BackingOff: "BackingOff",
  Error: "Error",
  Configured: "Configured",
  Disabled: "Disabled",
  Offline: "Offline",
} as const;

export const ConnectorReachability = {
  Unreachable: "Unreachable",
} as const;
