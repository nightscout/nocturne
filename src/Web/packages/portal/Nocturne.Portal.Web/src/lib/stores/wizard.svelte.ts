// Wizard state management using Svelte 5 runes

export interface WizardState {
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

function createWizardStore() {
  let state = $state<WizardState>({
    setupType: 'fresh',
    postgres: {
      useContainer: true
    },
    optionalServices: {
      watchtower: true
    },
    selectedConnectors: [],
    connectorConfigs: {}
  });

  return {
    get state() { return state; },

    setSetupType(type: WizardState['setupType']) {
      state.setupType = type;
    },

    setMigration(config: WizardState['migration']) {
      state.migration = config;
    },

    setCompatibilityProxy(config: WizardState['compatibilityProxy']) {
      state.compatibilityProxy = config;
    },

    setPostgres(config: WizardState['postgres']) {
      state.postgres = config;
    },

    setOptionalServices(config: WizardState['optionalServices']) {
      state.optionalServices = config;
    },

    toggleConnector(connectorType: string) {
      if (state.selectedConnectors.includes(connectorType)) {
        state.selectedConnectors = state.selectedConnectors.filter(c => c !== connectorType);
        delete state.connectorConfigs[connectorType];
      } else {
        state.selectedConnectors = [...state.selectedConnectors, connectorType];
        state.connectorConfigs[connectorType] = {};
      }
    },

    setConnectorConfig(connectorType: string, config: Record<string, string>) {
      state.connectorConfigs[connectorType] = config;
    },

    reset() {
      state = {
        setupType: 'fresh',
        postgres: { useContainer: true },
        optionalServices: { watchtower: true },
        selectedConnectors: [],
        connectorConfigs: {}
      };
    }
  };
}

export const wizardStore = createWizardStore();
