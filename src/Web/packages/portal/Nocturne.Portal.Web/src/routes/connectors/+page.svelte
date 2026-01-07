<script lang="ts">
  import { goto } from '$app/navigation';
  import { wizardStore } from '$lib/stores/wizard.svelte';
  import { fetchConnectors, type ConnectorMetadata } from '$lib/api/client';
  import { onMount } from 'svelte';

  let connectors = $state<ConnectorMetadata[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  onMount(async () => {
    try {
      const response = await fetchConnectors();
      connectors = response.connectors;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load connectors';
      // Fallback connectors for demo
      connectors = [
        { type: 'Dexcom', displayName: 'Dexcom', category: 'Cgm', description: 'Dexcom Share/Clarity', icon: 'dexcom', fields: [] },
        { type: 'LibreLinkUp', displayName: 'FreeStyle Libre', category: 'Cgm', description: 'LibreLinkUp', icon: 'libre', fields: [] },
        { type: 'Glooko', displayName: 'Glooko', category: 'Cgm', description: 'Glooko/Diasend', icon: 'glooko', fields: [] },
        { type: 'MiniMed', displayName: 'Medtronic CareLink', category: 'Pump', description: 'CareLink Connect', icon: 'minimed', fields: [] },
        { type: 'Nightscout', displayName: 'Nightscout', category: 'Data', description: 'Another Nightscout', icon: 'nightscout', fields: [] },
        { type: 'Tidepool', displayName: 'Tidepool', category: 'Data', description: 'Tidepool', icon: 'tidepool', fields: [] },
      ];
    } finally {
      loading = false;
    }
  });

  function isSelected(type: string): boolean {
    return wizardStore.state.selectedConnectors.includes(type);
  }

  function toggleConnector(type: string) {
    wizardStore.toggleConnector(type);
  }

  function getCategoryColor(category: string): string {
    switch (category.toLowerCase()) {
      case 'cgm': return 'text-green-400 bg-green-500/20';
      case 'pump': return 'text-blue-400 bg-blue-500/20';
      case 'data': return 'text-purple-400 bg-purple-500/20';
      default: return 'text-gray-400 bg-gray-500/20';
    }
  }

  function handleContinue() {
    goto('/download');
  }
</script>

<div class="max-w-4xl mx-auto">
  <div class="mb-8">
    <a href="/setup?type={wizardStore.state.setupType}" class="text-muted-foreground hover:text-foreground transition-colors text-sm flex items-center gap-1">
      <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m15 18-6-6 6-6"/></svg>
      Back to setup
    </a>
  </div>

  <h1 class="text-3xl font-bold mb-2">Select Connectors</h1>
  <p class="text-muted-foreground mb-8">Choose which data sources to enable. You can configure credentials later.</p>

  {#if loading}
    <div class="flex justify-center py-12">
      <div class="animate-spin w-8 h-8 border-2 border-primary border-t-transparent rounded-full"></div>
    </div>
  {:else if error}
    <div class="p-4 rounded-lg bg-amber-500/10 border border-amber-500/50 text-amber-400 mb-6">
      <p class="font-medium">Could not load connectors from API</p>
      <p class="text-sm opacity-80">Showing available connectors from local data</p>
    </div>
  {/if}

  <div class="grid md:grid-cols-2 gap-4 mb-8">
    {#each connectors as connector}
      <button
        onclick={() => toggleConnector(connector.type)}
        class="group p-5 rounded-xl border text-left transition-all"
        class:border-primary={isSelected(connector.type)}
        class:bg-primary/10={isSelected(connector.type)}
        class:border-border/50={!isSelected(connector.type)}
        class:bg-card/50={!isSelected(connector.type)}
        class:hover:border-primary/50={!isSelected(connector.type)}
      >
        <div class="flex items-start gap-4">
          <div class="w-10 h-10 rounded-lg bg-muted/50 flex items-center justify-center text-lg shrink-0">
            {connector.displayName.charAt(0)}
          </div>
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2 mb-1">
              <h3 class="font-semibold">{connector.displayName}</h3>
              <span class="text-xs px-2 py-0.5 rounded-full {getCategoryColor(connector.category)}">
                {connector.category}
              </span>
            </div>
            <p class="text-sm text-muted-foreground truncate">{connector.description}</p>
          </div>
          <div class="shrink-0">
            {#if isSelected(connector.type)}
              <div class="w-6 h-6 rounded-full bg-primary flex items-center justify-center">
                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>
              </div>
            {:else}
              <div class="w-6 h-6 rounded-full border-2 border-muted-foreground/30"></div>
            {/if}
          </div>
        </div>
      </button>
    {/each}
  </div>

  <div class="flex items-center justify-between">
    <p class="text-sm text-muted-foreground">
      {wizardStore.state.selectedConnectors.length} connector{wizardStore.state.selectedConnectors.length !== 1 ? 's' : ''} selected
    </p>
    <button
      onclick={handleContinue}
      class="px-6 py-3 rounded-lg bg-primary text-primary-foreground font-medium hover:bg-primary/90 transition-colors flex items-center gap-2"
    >
      Review & Download
      <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18 6-6-6-6"/></svg>
    </button>
  </div>
</div>
