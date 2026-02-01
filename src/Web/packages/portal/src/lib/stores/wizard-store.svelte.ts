/**
 * Shared store for wizard state that shouldn't be in the URL (sensitive data).
 * This persists across page navigation within the SPA.
 */

// Connector configurations (credentials, API keys, etc.)
let connectorConfigs = $state<Record<string, Record<string, string>>>({});

export function getConnectorConfigs() {
    return connectorConfigs;
}

export function setConnectorConfigs(configs: Record<string, Record<string, string>>) {
    connectorConfigs = configs;
}

export function updateConnectorConfig(connectorType: string, values: Record<string, string>) {
    connectorConfigs = {
        ...connectorConfigs,
        [connectorType]: values,
    };
}

export function removeConnectorConfig(connectorType: string) {
    const { [connectorType]: _, ...rest } = connectorConfigs;
    connectorConfigs = rest;
}

export function hasConnectorConfig(connectorType: string): boolean {
    const config = connectorConfigs[connectorType];
    if (!config) return false;
    return Object.values(config).some((v) => v && v.trim() !== "");
}
