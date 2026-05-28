<script lang="ts">
  import type {
    AvailableConnector,
    ServicesOverview,
  } from "$lib/api/generated/nocturne-api-client";
  import { Card, CardContent } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    AlertCircle,
    CheckCircle,
    ChevronRight,
    Plug,
  } from "lucide-svelte";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";

  interface Props {
    servicesOverview: ServicesOverview | null;
    isLoading: boolean;
    error: string | null;
    onSelect: (connector: AvailableConnector) => void;
    onCancel?: () => void;
  }

  const { servicesOverview, isLoading, error, onSelect, onCancel }: Props =
    $props();
</script>

{#if isLoading}
  <SettingsPageSkeleton cardCount={2} />
{:else if error}
  <Card class="border-destructive">
    <CardContent class="flex items-center gap-3 pt-6">
      <AlertCircle class="h-5 w-5 text-destructive" />
      <div>
        <p class="font-medium">Error</p>
        <p class="text-sm text-muted-foreground">{error}</p>
      </div>
    </CardContent>
  </Card>
{:else if servicesOverview?.availableConnectors}
  <div class="@container space-y-4">
    <div>
      <h3 class="text-lg font-semibold">Choose a connector</h3>
      <p class="text-sm text-muted-foreground">
        Select a data source to configure
      </p>
    </div>
    <div class="grid gap-3 @xl:grid-cols-2">
      {#each servicesOverview.availableConnectors as connector}
        <button
          class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {connector.isConfigured
            ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
            : ''}"
          onclick={() => onSelect(connector)}
        >
          <div
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {connector.isConfigured
              ? 'bg-green-100 dark:bg-green-900/30'
              : 'bg-primary/10'}"
          >
            <AppLogo icon={connector.icon} invertMode />
          </div>
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="font-medium">{connector.name}</span>
              {#if connector.isConfigured}
                <Badge
                  class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                >
                  <CheckCircle class="h-3 w-3 mr-1" />
                  Configured
                </Badge>
              {/if}
            </div>
            {#if connector.description}
              <p class="text-sm text-muted-foreground truncate">
                {connector.description}
              </p>
            {/if}
          </div>
          <ChevronRight
            class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
          />
        </button>
      {/each}
    </div>
  </div>
{:else}
  <Card>
    <CardContent class="py-8 text-center">
      <Plug class="h-12 w-12 mx-auto mb-4 text-muted-foreground" />
      <p class="font-medium">No connectors available</p>
      <p class="text-sm text-muted-foreground mt-2">
        No server-side connectors are registered in this installation.
      </p>
    </CardContent>
  </Card>
{/if}

{#if onCancel}
  <div class="flex justify-start pt-2">
    <Button variant="ghost" onclick={onCancel}>Cancel</Button>
  </div>
{/if}
