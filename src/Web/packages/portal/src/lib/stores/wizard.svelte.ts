// Wizard state management using Svelte 5 class with $state

export interface WizardStateData {
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
  selectedConnectors: string[];
  connectorConfigs: Record<string, Record<string, string>>;
}

class WizardStore {
  setupType = $state<'fresh' | 'migrate' | 'compatibility-proxy'>('fresh');
  migration = $state<WizardStateData['migration']>(undefined);
  compatibilityProxy = $state<WizardStateData['compatibilityProxy']>(undefined);
  postgres = $state<WizardStateData['postgres']>({ useContainer: true });
  optionalServices = $state<WizardStateData['optionalServices']>({ watchtower: true });
  selectedConnectors = $state<string[]>([]);
  connectorConfigs = $state<Record<string, Record<string, string>>>({});

  setSetupType(type: WizardStateData['setupType']) {
    this.setupType = type;
  }

  setMigration(config: WizardStateData['migration']) {
    this.migration = config;
  }

  setCompatibilityProxy(config: WizardStateData['compatibilityProxy']) {
    this.compatibilityProxy = config;
  }

  setPostgres(config: WizardStateData['postgres']) {
    this.postgres = config;
  }

  setOptionalServices(config: WizardStateData['optionalServices']) {
    this.optionalServices = config;
  }

  toggleConnector(connectorType: string) {
    if (this.selectedConnectors.includes(connectorType)) {
      this.selectedConnectors = this.selectedConnectors.filter(c => c !== connectorType);
      const { [connectorType]: _, ...rest } = this.connectorConfigs;
      this.connectorConfigs = rest;
    } else {
      this.selectedConnectors = [...this.selectedConnectors, connectorType];
      this.connectorConfigs = { ...this.connectorConfigs, [connectorType]: {} };
    }
  }

  setConnectorConfig(connectorType: string, config: Record<string, string>) {
    this.connectorConfigs = { ...this.connectorConfigs, [connectorType]: config };
  }

  reset() {
    this.setupType = 'fresh';
    this.migration = undefined;
    this.compatibilityProxy = undefined;
    this.postgres = { useContainer: true };
    this.optionalServices = { watchtower: true };
    this.selectedConnectors = [];
    this.connectorConfigs = {};
  }
}

export const wizardStore = new WizardStore();
