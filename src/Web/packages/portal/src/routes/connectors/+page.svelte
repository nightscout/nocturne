<script lang="ts">
  import { wizardStore } from "$lib/stores/wizard.svelte";
  import { getConnectors } from "$lib/data/portal.remote";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { ChevronLeft, ChevronRight, Check } from "@lucide/svelte";

  const connectorsQuery = getConnectors({});

  function isSelected(type: string): boolean {
    return wizardStore.selectedConnectors.includes(type);
  }

  function toggleConnector(type: string) {
    wizardStore.toggleConnector(type);
  }

  function getCategoryVariant(
    category: string,
  ): "default" | "secondary" | "destructive" | "outline" {
    switch (category.toLowerCase()) {
      case "cgm":
        return "default";
      case "pump":
        return "secondary";
      default:
        return "outline";
    }
  }
</script>

<div class="max-w-4xl mx-auto">
  <Button
    href="/setup?type={wizardStore.setupType}"
    variant="ghost"
    size="sm"
    class="mb-8 gap-1 text-muted-foreground"
  >
    <ChevronLeft size={16} />
    Back to setup
  </Button>

  <h1 class="text-3xl font-bold mb-2">Select Connectors</h1>
  <p class="text-muted-foreground mb-8">Choose which data sources to enable</p>

  <svelte:boundary
    onerror={(e) => console.error("Connectors boundary error:", e)}
  >
    {#await connectorsQuery}
      <div class="flex justify-center py-12">
        <div
          class="animate-spin w-8 h-8 border-2 border-primary border-t-transparent rounded-full"
        ></div>
      </div>
    {:then connectors}
      <div class="grid md:grid-cols-2 gap-4 mb-8">
        {#each connectors as connector}
          <button
            onclick={() => toggleConnector(connector.type)}
            class="group p-5 rounded-xl border text-left transition-all {isSelected(
              connector.type,
            )
              ? 'border-primary bg-primary/10'
              : 'border-border/50 bg-card/50 hover:border-primary/50'}"
          >
            <div class="flex items-start gap-4">
              <div
                class="w-10 h-10 rounded-lg bg-muted/50 flex items-center justify-center text-lg shrink-0"
              >
                {connector.displayName.charAt(0)}
              </div>
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 mb-1">
                  <h3 class="font-semibold">{connector.displayName}</h3>
                  <Badge variant={getCategoryVariant(connector.category)}
                    >{connector.category}</Badge
                  >
                </div>
                <p class="text-sm text-muted-foreground truncate">
                  {connector.description}
                </p>
              </div>
              <div class="shrink-0">
                {#if isSelected(connector.type)}
                  <div
                    class="w-6 h-6 rounded-full bg-primary flex items-center justify-center text-primary-foreground"
                  >
                    <Check size={14} strokeWidth={3} />
                  </div>
                {:else}
                  <div
                    class="w-6 h-6 rounded-full border-2 border-muted-foreground/30"
                  ></div>
                {/if}
              </div>
            </div>
          </button>
        {/each}
      </div>

      <div class="flex items-center justify-between">
        <p class="text-sm text-muted-foreground">
          {wizardStore.selectedConnectors.length} connector{wizardStore
            .selectedConnectors.length !== 1
            ? "s"
            : ""} selected
        </p>
        <Button href="/download" class="gap-2">
          Review & Download
          <ChevronRight size={20} />
        </Button>
      </div>
    {:catch error}
      <div
        class="p-4 rounded-lg bg-destructive/10 border border-destructive/50 text-destructive mb-6"
      >
        <p class="font-medium">Could not load connectors</p>
        <p class="text-sm opacity-80">{error.message}</p>
      </div>
    {/await}
  </svelte:boundary>
</div>
