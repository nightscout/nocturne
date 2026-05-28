<script lang="ts">
  import {
    UploaderPlatform,
    UploaderCategory,
  } from "$lib/api/generated/nocturne-api-client";
  import type {
    UploaderApp,
    DataSourceInfo,
    AvailableConnector,
  } from "$lib/api/generated/nocturne-api-client";
  import { getUploaderName, getUploaderDescription } from "$lib/utils/uploader-labels";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    AlertCircle,
    ChevronRight,
    Loader2,
    Smartphone,
    Cloud,
    Plug,
  } from "lucide-svelte";
  import Apple from "lucide-svelte/icons/apple";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";

  interface Props {
    connectors: AvailableConnector[];
    uploaderApps: UploaderApp[];
    dataSources: DataSourceInfo[];
    isLoading: boolean;
    loadError: string | null;
    onSelectConnector: (id: string) => void;
    onSelectUploader: (app: UploaderApp) => void;
    onSkip: () => void;
  }

  const {
    connectors = [],
    uploaderApps = [],
    dataSources = [],
    isLoading = false,
    loadError = null,
    onSelectConnector,
    onSelectUploader,
    onSkip,
  }: Props = $props();

  type PlatformFilter = "all" | UploaderPlatform;
  let platformFilter = $state<PlatformFilter>("all");

  const categoryLabels: Record<string, string> = {
    [UploaderCategory.Cgm]: "CGM Apps",
    [UploaderCategory.AidSystem]: "AID Systems",
    [UploaderCategory.Uploader]: "General Uploaders",
  };

  const categoryOrder = [UploaderCategory.Cgm, UploaderCategory.AidSystem, UploaderCategory.Uploader];

  const filteredApps = $derived(
    platformFilter === "all"
      ? uploaderApps
      : uploaderApps.filter((app) => app.platform === platformFilter),
  );

  const groupedApps = $derived.by(() => {
    const groups: Record<string, UploaderApp[]> = {};
    for (const app of filteredApps) {
      const cat = app.category ?? (UploaderCategory.Uploader as string);
      if (!groups[cat]) groups[cat] = [];
      groups[cat].push(app);
    }
    return categoryOrder
      .filter((cat) => groups[cat]?.length)
      .map((cat) => ({ category: cat, label: categoryLabels[cat] ?? cat, apps: groups[cat] }));
  });

  function isDetected(appId: string | undefined): boolean {
    if (!appId) return false;
    return dataSources.some(
      (ds) => ds.sourceType?.toLowerCase() === appId.toLowerCase(),
    );
  }

  function getPlatformLabel(platform: UploaderPlatform | undefined): string {
    switch (platform) {
      case UploaderPlatform.IOS:
        return "iOS";
      case UploaderPlatform.Android:
        return "Android";
      case UploaderPlatform.Desktop:
        return "Desktop";
      case UploaderPlatform.Web:
        return "Web";
      default:
        return "Unknown";
    }
  }

</script>

<div>
  <h1 class="text-2xl font-bold tracking-tight">Connect a Data Source</h1>
  <p class="text-muted-foreground">
    Choose a cloud service or phone app to start sending glucose and treatment data to Nocturne.
  </p>
</div>

{#if isLoading}
  <div class="flex items-center justify-center py-12">
    <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
  </div>
{:else if loadError}
  <Card.Root class="border-destructive">
    <Card.Content class="flex items-center gap-3 pt-6">
      <AlertCircle class="h-5 w-5 text-destructive" />
      <div>
        <p class="font-medium">Failed to load data sources</p>
        <p class="text-sm text-muted-foreground">{loadError}</p>
      </div>
    </Card.Content>
  </Card.Root>
{:else}
  <div class="@container space-y-8">
    <!-- Cloud Services (Connectors) -->
    {#if connectors.length > 0}
      <div class="space-y-3">
        <h2 class="text-sm font-medium text-muted-foreground flex items-center gap-2">
          <Cloud class="h-4 w-4" />
          Cloud Services
        </h2>
        <div class="grid gap-3 @xl:grid-cols-2">
          {#each connectors as connector (connector.id)}
            {@const configured = connector.isConfigured ?? false}
            <button
              type="button"
              class="flex items-center gap-4 p-4 rounded-lg border transition-colors text-left group {configured
                ? 'border-green-500/30 bg-green-500/5 hover:bg-green-500/10'
                : 'bg-muted/30 hover:border-primary/50 hover:bg-accent/50'}"
              onclick={() => onSelectConnector(connector.id ?? "")}
            >
              <div
                class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {configured
                  ? 'bg-green-500/10 text-green-600'
                  : 'bg-primary/10 text-primary'}"
              >
                <AppLogo icon={connector.icon} invertMode />
              </div>
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 flex-wrap">
                  <span class="font-medium">{connector.name}</span>
                  {#if configured}
                    <Badge variant="secondary" class="text-xs text-green-600">
                      Connected
                    </Badge>
                  {/if}
                </div>
                {#if connector.description}
                  <p class="text-sm text-muted-foreground line-clamp-1">
                    {connector.description}
                  </p>
                {/if}
              </div>
              <ChevronRight
                class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors shrink-0"
              />
            </button>
          {/each}
        </div>
      </div>
    {/if}

    <!-- Phone Apps (Uploaders) -->
    {#if uploaderApps.length > 0}
      <div class="space-y-3">
        <div class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between">
          <h2 class="text-sm font-medium text-muted-foreground flex items-center gap-2">
            <Smartphone class="h-4 w-4" />
            Phone Apps
          </h2>
          <div class="flex flex-wrap items-center gap-1 shrink-0">
            <Button
              variant={platformFilter === "all" ? "default" : "outline"}
              size="sm"
              onclick={() => (platformFilter = "all")}
            >
              All
            </Button>
            <Button
              variant={platformFilter === UploaderPlatform.IOS ? "default" : "outline"}
              size="sm"
              class="gap-1.5"
              onclick={() => (platformFilter = UploaderPlatform.IOS)}
            >
              <Apple class="h-3.5 w-3.5" />
              iOS
            </Button>
            <Button
              variant={platformFilter === UploaderPlatform.Android ? "default" : "outline"}
              size="sm"
              class="gap-1.5"
              onclick={() => (platformFilter = UploaderPlatform.Android)}
            >
              <Smartphone class="h-3.5 w-3.5" />
              Android
            </Button>
          </div>
        </div>

        <div class="space-y-6">
          {#each groupedApps as group (group.category)}
            <div class="space-y-3">
              <h3 class="text-sm font-medium text-muted-foreground">{group.label}</h3>
              <div class="grid gap-3 @xl:grid-cols-2">
                {#each group.apps as app (app.id)}
                  {@const detected = isDetected(app.id)}
                  <button
                    type="button"
                    class="flex items-center gap-4 p-4 rounded-lg border transition-colors text-left group {detected
                      ? 'border-green-500/30 bg-green-500/5 hover:bg-green-500/10'
                      : 'bg-muted/30 hover:border-primary/50 hover:bg-accent/50'}"
                    onclick={() => onSelectUploader(app)}
                  >
                    <div
                      class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {detected
                        ? 'bg-green-500/10 text-green-600'
                        : 'bg-primary/10 text-primary'}"
                    >
                      <AppLogo icon={app.icon} invertMode />
                    </div>
                    <div class="flex-1 min-w-0">
                      <div class="flex items-center gap-2 flex-wrap">
                        <span class="font-medium">{getUploaderName(app)}</span>
                        <Badge variant="outline" class="text-xs gap-1">
                          {getPlatformLabel(app.platform)}
                        </Badge>
                        {#if detected}
                          <Badge variant="secondary" class="text-xs text-green-600">
                            Connected
                          </Badge>
                        {/if}
                      </div>
                      {#if getUploaderDescription(app)}
                        <p class="text-sm text-muted-foreground line-clamp-1">
                          {getUploaderDescription(app)}
                        </p>
                      {/if}
                    </div>
                    <ChevronRight
                      class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors shrink-0"
                    />
                  </button>
                {/each}
              </div>
            </div>
          {/each}
        </div>
      </div>
    {/if}

    <!-- Empty state when both are empty -->
    {#if connectors.length === 0 && uploaderApps.length === 0}
      <Card.Root>
        <Card.Content class="py-8 text-center">
          <Plug class="h-12 w-12 mx-auto mb-4 text-muted-foreground" />
          <p class="font-medium">No data sources available</p>
          <p class="text-sm text-muted-foreground mt-1">
            There are no data sources available at this time.
          </p>
        </Card.Content>
      </Card.Root>
    {/if}
  </div>

  <!-- Skip link -->
  <div class="pt-4 text-center">
    <button
      type="button"
      class="text-sm text-muted-foreground hover:text-foreground underline-offset-4 hover:underline transition-colors"
      onclick={onSkip}
    >
      Skip for now
    </button>
  </div>
{/if}
