// API client for Portal backend

export interface ConnectorField {
  name: string;
  envVar: string;
  type: 'string' | 'password' | 'boolean' | 'select' | 'number';
  required: boolean;
  description: string;
  default?: string;
  options?: string[];
}

export interface ConnectorMetadata {
  type: string;
  displayName: string;
  category: string;
  description: string;
  icon: string;
  fields: ConnectorField[];
}

export interface ConnectorsResponse {
  connectors: ConnectorMetadata[];
}

export interface GenerateRequest {
  setupType: 'fresh' | 'migrate' | 'compatibility-proxy';
  migration?: {
    nightscoutUrl: string;
    nightscoutApiSecret: string;
  };
  compatibilityProxy?: {
    nightscoutUrl: string;
    nightscoutApiSecret: string;
  };
  postgres: {
    useContainer: boolean;
    connectionString?: string;
  };
  optionalServices: {
    watchtower: boolean;
  };
  connectors: Array<{
    type: string;
    config: Record<string, string>;
  }>;
}

const API_BASE = '/api';

export async function fetchConnectors(): Promise<ConnectorsResponse> {
  const response = await fetch(`${API_BASE}/connectors`);
  if (!response.ok) {
    throw new Error(`Failed to fetch connectors: ${response.statusText}`);
  }
  return response.json();
}

export async function generateConfig(request: GenerateRequest): Promise<Blob> {
  const response = await fetch(`${API_BASE}/generate`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(request)
  });

  if (!response.ok) {
    const error = await response.text();
    throw new Error(`Failed to generate config: ${error}`);
  }

  return response.blob();
}
