<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { ChevronLeft } from "lucide-svelte";
  import ConnectorSetup from "$lib/components/connectors/ConnectorSetup.svelte";

  const connectorName = $derived(page.params.connector);
  const isHomeAssistant = $derived(
    connectorName?.toLowerCase() === "homeassistant"
  );
</script>

<svelte:head>
  <title>{connectorName} - Connectors - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-3xl space-y-6">
  <div>
    <Button
      variant="ghost"
      size="sm"
      href="/settings/connectors"
      class="gap-1 -ml-2 mb-4"
    >
      <ChevronLeft class="h-4 w-4" />
      Back to connectors
    </Button>
  </div>

  <ConnectorSetup
    connectorId={connectorName}
    showToggle
    showDangerZone
    showCapabilities
    primaryAction="save-only"
    onCancel={() => goto("/settings/connectors")}
  >
    {#snippet extras()}
      {#if isHomeAssistant}
        <Card>
          <CardHeader>
            <CardTitle>Setup Guide</CardTitle>
            <CardDescription>
              Connect Home Assistant to Nocturne using the HACS custom integration
            </CardDescription>
          </CardHeader>
          <CardContent>
            <ol class="space-y-4">
              <li class="flex gap-4">
                <div
                  class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                >
                  1
                </div>
                <div class="flex-1 pt-1">
                  <p class="font-medium">Install HACS</p>
                  <p class="text-sm text-muted-foreground mt-1">
                    If you haven't already, install the Home Assistant Community Store (HACS) by following the instructions at hacs.xyz.
                  </p>
                </div>
              </li>
              <li class="flex gap-4">
                <div
                  class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                >
                  2
                </div>
                <div class="flex-1 pt-1">
                  <p class="font-medium">Add the Nocturne repository</p>
                  <p class="text-sm text-muted-foreground mt-1">
                    In HACS, open the menu and select "Custom repositories". Add the Nocturne Home Assistant repository URL and select "Integration" as the category.
                  </p>
                </div>
              </li>
              <li class="flex gap-4">
                <div
                  class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                >
                  3
                </div>
                <div class="flex-1 pt-1">
                  <p class="font-medium">Install the integration</p>
                  <p class="text-sm text-muted-foreground mt-1">
                    Search for "Nocturne" in HACS and install it. Once installed, restart Home Assistant to load the integration.
                  </p>
                </div>
              </li>
              <li class="flex gap-4">
                <div
                  class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                >
                  4
                </div>
                <div class="flex-1 pt-1">
                  <p class="font-medium">Add the Nocturne integration</p>
                  <p class="text-sm text-muted-foreground mt-1">
                    Go to Settings, then Devices & Services, then Add Integration. Search for "Nocturne" and select it.
                  </p>
                </div>
              </li>
              <li class="flex gap-4">
                <div
                  class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                >
                  5
                </div>
                <div class="flex-1 pt-1">
                  <p class="font-medium">Connect to your Nocturne instance</p>
                  <p class="text-sm text-muted-foreground mt-1">
                    Enter your Nocturne instance URL and complete the OAuth authorization flow. Sensors will be created automatically based on your available data.
                  </p>
                </div>
              </li>
            </ol>
          </CardContent>
        </Card>
      {/if}
    {/snippet}
  </ConnectorSetup>
</div>
