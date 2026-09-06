<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import {
    Loader2,
    Plug,
    KeyRound,
    Download,
    ArrowRight,
    Wifi,
  } from "lucide-svelte";
  import { resolve } from "$app/paths";
  import type { ConnectorStatusDto } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    /**
     * Connectors that have a configuration but have not yet produced any
     * reading. The caller passes only configured connectors, and only reaches
     * this state when every one of them has imported zero records.
     */
    connectors?: ConnectorStatusDto[];
  }

  let { connectors = [] }: Props = $props();

  const hasConnector = $derived(connectors.length > 0);

  /**
   * A connector has run a sync once it records an attempt or a success. With no
   * records to show, that is the only thing separating "synced, nothing there
   * yet" from "hasn't synced yet" — the health flag reports both as healthy.
   */
  function hasSynced(connector: ConnectorStatusDto): boolean {
    return Boolean(connector.lastSyncAttempt || connector.lastSuccessfulSync);
  }

  const connectorsPath = resolve("/settings/connectors");
  const uploaderTokenPath = `${resolve("/settings/connectors")}#api-tokens-section`;
  const migrationPath = resolve("/settings/migration");
</script>

<Card data-testid="first-reading-empty-state">
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Wifi class="h-5 w-5 text-primary" />
      Waiting for your first reading
    </CardTitle>
    <CardDescription>
      Your glucose chart will appear here as soon as the first reading arrives.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    {#if hasConnector}
      <div class="space-y-3" data-testid="waiting-connectors">
        {#each connectors as connector (connector.id ?? connector.name)}
          <div
            class="flex items-start gap-4 rounded-lg border bg-card p-4"
            data-testid="waiting-connector"
          >
            <div
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
            >
              <Loader2 class="h-5 w-5 animate-spin text-primary" />
            </div>
            <div class="min-w-0 flex-1">
              <h4 class="font-medium">
                Waiting for the first sync from {connector.name ??
                  "your connector"}
              </h4>
              {#if hasSynced(connector)}
                {#if connector.stateMessage}
                  <p
                    class="mt-1 text-sm text-muted-foreground"
                    data-testid="connector-outcome"
                  >
                    Last sync: {connector.stateMessage}
                  </p>
                {:else}
                  <p
                    class="mt-1 text-sm text-muted-foreground"
                    data-testid="connector-synced-empty"
                  >
                    It synced, but no readings have come through yet. New data
                    can take a little while to appear.
                  </p>
                {/if}
              {:else}
                <p
                  class="mt-1 text-sm text-muted-foreground"
                  data-testid="connector-not-synced"
                >
                  It hasn't run its first sync yet. This can take a few minutes.
                </p>
              {/if}
            </div>
          </div>
        {/each}
        <Button variant="outline" href={connectorsPath} class="gap-2">
          <Plug class="h-4 w-4" />
          Manage connectors
          <ArrowRight class="h-4 w-4" />
        </Button>
      </div>
    {:else}
      <p class="text-sm text-muted-foreground" data-testid="no-connector-intro">
        No data source is set up yet. Pick whichever matches how you track your
        glucose.
      </p>
      <div class="grid gap-3 @md:grid-cols-3">
        <a
          href={connectorsPath}
          data-testid="path-connector"
          class="group flex flex-col gap-2 rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-muted/40"
        >
          <Plug class="h-5 w-5 text-primary" />
          <span class="font-medium">Connect a CGM or pump account</span>
          <span class="text-sm text-muted-foreground">
            Pull data automatically from services like Dexcom, LibreLink, or
            Glooko.
          </span>
        </a>
        <!-- eslint-disable svelte/no-navigation-without-resolve -- resolve() covers the route; the #api-tokens-section fragment deep-links to the token section and cannot be expressed through resolve() -->
        <a
          href={uploaderTokenPath}
          data-testid="path-uploader"
          class="group flex flex-col gap-2 rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-muted/40"
        >
          <KeyRound class="h-5 w-5 text-primary" />
          <span class="font-medium">Set up an uploader app</span>
          <span class="text-sm text-muted-foreground">
            Create an API token so an app like xDrip, AAPS, Loop, or Trio can
            send readings.
          </span>
        </a>
        <!-- eslint-enable svelte/no-navigation-without-resolve -->
        <a
          href={migrationPath}
          data-testid="path-migration"
          class="group flex flex-col gap-2 rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-muted/40"
        >
          <Download class="h-5 w-5 text-primary" />
          <span class="font-medium">Coming from Nightscout?</span>
          <span class="text-sm text-muted-foreground">
            Import your existing history from a Nightscout site.
          </span>
        </a>
      </div>
    {/if}
  </CardContent>
</Card>
