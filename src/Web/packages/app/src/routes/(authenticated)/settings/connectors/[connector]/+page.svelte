<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { Button } from "#lib/components/ui/button/index.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import ConnectorSetup from "#lib/components/connectors/ConnectorSetup.svelte";
  import { getMyPermissions } from "#lib/api/generated/myPermissions.generated.remote.js";
  import { canChangeTenantSettings } from "#lib/authorization/tenant-settings.js";
  import { canSetUpConnectors } from "#lib/authorization/connector-setup.js";
  import OuraImportCard from "#lib/components/connectors/OuraImportCard.svelte";
  import ReadOnlyNotice from "#lib/components/settings/ReadOnlyNotice.svelte";
  import RepullHistoryCard from "./RepullHistoryCard.svelte";
  import type { AvailableConnector } from "@nocturne/api-client";

  const connectorName = $derived(page.params.connector);
  const isHomeAssistant = $derived(
    connectorName?.toLowerCase() === "homeassistant"
  );
  // Oura has no sync to enable, configure or repeat: its data arrives as an uploaded export.
  const isOura = $derived(connectorName?.toLowerCase() === "oura");
  const permissionsQuery = getMyPermissions();
  const canManage = $derived(
    canChangeTenantSettings(
      permissionsQuery.current?.scopes,
      permissionsQuery.current?.refusedAsDemoSubject
    )
  );
  const canSetUp = $derived(
    canSetUpConnectors(
      permissionsQuery.current?.scopes,
      permissionsQuery.current?.refusedAsDemoSubject
    )
  );
</script>

<svelte:head>
  <title>{connectorName} - Connectors - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto p-3 @md:p-6 max-w-3xl space-y-6">
  <div>
    <Button
      variant="ghost"
      size="sm"
      href="/settings/connectors"
      class="-ml-2 mb-4"
    >
      <ChevronLeft class="h-4 w-4" />
      Back to connectors
    </Button>
  </div>

  <ConnectorSetup
    connectorId={connectorName}
    showToggle={!isOura}
    showConfiguration={!isOura}
    showDangerZone
    showCapabilities={!isOura}
    primaryAction="save-only"
    showEnvVarHints={page.data.isPlatformAdmin === true}
    onCancel={() => goto(resolve('settings/connectors'))}
  >
    {#snippet extras({ connector }: { connector: AvailableConnector })}
      {#if isOura && canManage && connectorName}
        <OuraImportCard connectorId={connectorName} />
      {:else if isOura && canSetUp && !canManage}
        <ReadOnlyNotice data-testid="oura-import-read-only">
          Only members who can manage this site's settings can import Oura data.
        </ReadOnlyNotice>
      {:else if connector.id && !isOura}
        <RepullHistoryCard connectorId={connector.id} />
      {/if}

      {#if isHomeAssistant}
        <Card>
          <CardHeader>
            <CardTitle>Setup guide</CardTitle>
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
                    If you do not have the Home Assistant Community Store (HACS), install it with the instructions at hacs.xyz.
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
