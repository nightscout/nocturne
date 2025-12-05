<script lang="ts">
  import { onMount } from "svelte";
  import {
    getServicesOverview,
    getUploaderSetup,
    deleteDemoData as deleteDemoDataRemote,
    deleteDataSourceData as deleteDataSourceDataRemote,
  } from "$lib/data/services.remote";
  import type {
    ServicesOverview,
    UploaderApp,
    UploaderSetupResponse,
    DataSourceInfo,
    AvailableConnector,
  } from "$lib/api/generated/nocturne-api-client";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import {
    Plug,
    RefreshCw,
    CheckCircle,
    AlertCircle,
    Clock,
    ExternalLink,
    Smartphone,
    Cloud,
    Database,
    Loader2,
    Copy,
    Check,
    Activity,
    Wifi,
    WifiOff,
    Settings,
    ChevronRight,
    Trash2,
    AlertTriangle,
    Sparkles,
    Pencil,
  } from "lucide-svelte";
  import Apple from "lucide-svelte/icons/apple";
  import TabletSmartphone from "lucide-svelte/icons/tablet-smartphone";

  let servicesOverview = $state<ServicesOverview | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let selectedUploader = $state<UploaderApp | null>(null);
  let uploaderSetup = $state<UploaderSetupResponse | null>(null);
  let showSetupDialog = $state(false);
  let copiedField = $state<string | null>(null);

  // Demo data dialog state
  let showDemoDataDialog = $state(false);
  let isDeletingDemo = $state(false);
  let demoDeleteResult = $state<{
    success: boolean;
    entriesDeleted?: number;
    error?: string;
  } | null>(null);

  // Data source management dialog state
  let selectedDataSource = $state<DataSourceInfo | null>(null);
  let showManageDataSourceDialog = $state(false);
  let showDeleteConfirmDialog = $state(false);
  let isDeletingDataSource = $state(false);
  let deleteConfirmText = $state("");
  let deleteResult = $state<{
    success: boolean;
    entriesDeleted?: number;
    error?: string;
  } | null>(null);

  onMount(async () => {
    await loadServices();
  });

  async function loadServices() {
    isLoading = true;
    error = null;
    try {
      servicesOverview = await getServicesOverview();
    } catch (e) {
      error = e instanceof Error ? e.message : "Failed to load services";
    } finally {
      isLoading = false;
    }
  }

  async function openUploaderSetup(uploader: UploaderApp) {
    selectedUploader = uploader;
    showSetupDialog = true;

    try {
      uploaderSetup = await getUploaderSetup(uploader.id!);
    } catch (e) {
      console.error("Failed to load setup instructions", e);
    }
  }

  function isDemoDataSource(source: DataSourceInfo): boolean {
    return source.category === "demo" || source.sourceType === "demo";
  }

  function openDataSourceDialog(source: DataSourceInfo) {
    if (isDemoDataSource(source)) {
      showDemoDataDialog = true;
      demoDeleteResult = null;
    } else {
      selectedDataSource = source;
      showManageDataSourceDialog = true;
      deleteResult = null;
    }
  }

  function openDeleteConfirmation() {
    showManageDataSourceDialog = false;
    showDeleteConfirmDialog = true;
    deleteConfirmText = "";
    deleteResult = null;
  }

  async function deleteDemoData() {
    isDeletingDemo = true;
    demoDeleteResult = null;
    try {
      const result = await deleteDemoDataRemote();
      demoDeleteResult = {
        success: result.success ?? false,
        entriesDeleted: result.entriesDeleted,
        error: result.error ?? undefined,
      };
      if (result.success) {
        await loadServices();
      }
    } catch (e) {
      demoDeleteResult = {
        success: false,
        error: e instanceof Error ? e.message : "Failed to delete demo data",
      };
    } finally {
      isDeletingDemo = false;
    }
  }

  async function deleteDataSource() {
    if (!selectedDataSource) return;

    isDeletingDataSource = true;
    deleteResult = null;
    try {
      const result = await deleteDataSourceDataRemote(selectedDataSource.id!);
      deleteResult = {
        success: result.success ?? false,
        entriesDeleted: result.entriesDeleted,
        error: result.error ?? undefined,
      };
      if (result.success) {
        showDeleteConfirmDialog = false;
        await loadServices();
      }
    } catch (e) {
      deleteResult = {
        success: false,
        error: e instanceof Error ? e.message : "Failed to delete data",
      };
    } finally {
      isDeletingDataSource = false;
    }
  }

  // Check if a data source matches an uploader or connector
  function getMatchingUploader(source: DataSourceInfo): UploaderApp | null {
    if (!servicesOverview?.uploaderApps) return null;

    const sourceLower = (source.sourceType ?? source.name ?? "").toLowerCase();
    const deviceLower = (source.deviceId ?? "").toLowerCase();

    for (const uploader of servicesOverview.uploaderApps) {
      const uploaderIdLower = (uploader.id ?? "").toLowerCase();

      // Match by source type
      if (sourceLower === uploaderIdLower) return uploader;

      // Match xDrip+ variations
      if (uploaderIdLower === "xdrip") {
        if (sourceLower.includes("xdrip") || deviceLower.includes("xdrip")) {
          return uploader;
        }
      }

      // Match Loop
      if (uploaderIdLower === "loop") {
        if (
          (sourceLower === "loop" || deviceLower.includes("loop")) &&
          !sourceLower.includes("openaps")
        ) {
          return uploader;
        }
      }

      // Match AAPS/AndroidAPS
      if (uploaderIdLower === "aaps") {
        if (
          sourceLower.includes("aaps") ||
          sourceLower.includes("androidaps") ||
          deviceLower.includes("aaps") ||
          deviceLower.includes("androidaps")
        ) {
          return uploader;
        }
      }

      // Match Trio
      if (uploaderIdLower === "trio") {
        if (sourceLower === "trio" || deviceLower.includes("trio")) {
          return uploader;
        }
      }

      // Match iAPS
      if (uploaderIdLower === "iaps") {
        if (sourceLower === "iaps" || deviceLower.includes("iaps")) {
          return uploader;
        }
      }

      // Match Spike
      if (uploaderIdLower === "spike") {
        if (sourceLower.includes("spike") || deviceLower.includes("spike")) {
          return uploader;
        }
      }
    }

    return null;
  }

  function getMatchingConnector(
    source: DataSourceInfo
  ): AvailableConnector | null {
    if (!servicesOverview?.availableConnectors) return null;

    const sourceLower = (source.sourceType ?? "").toLowerCase();

    for (const connector of servicesOverview.availableConnectors) {
      const connectorIdLower = (connector.id ?? "").toLowerCase();

      // Match Dexcom connector
      if (
        connectorIdLower === "dexcom" &&
        sourceLower.includes("dexcom-connector")
      ) {
        return connector;
      }

      // Match Libre connector
      if (
        connectorIdLower === "libre" &&
        sourceLower.includes("libre-connector")
      ) {
        return connector;
      }

      // Match Nightscout bridge
      if (
        connectorIdLower === "nightscout" &&
        sourceLower.includes("nightscout-connector")
      ) {
        return connector;
      }
    }

    return null;
  }

  function isUploaderActive(uploader: UploaderApp): boolean {
    if (!servicesOverview?.activeDataSources) return false;

    for (const source of servicesOverview.activeDataSources) {
      const matchingUploader = getMatchingUploader(source);
      if (matchingUploader?.id === uploader.id) {
        return true;
      }
    }

    return false;
  }

  function isConnectorActive(connector: AvailableConnector): boolean {
    if (!servicesOverview?.activeDataSources) return false;

    for (const source of servicesOverview.activeDataSources) {
      const matchingConnector = getMatchingConnector(source);
      if (matchingConnector?.id === connector.id) {
        return true;
      }
    }

    return false;
  }

  function getStatusBadge(status: string | undefined): {
    variant: "default" | "secondary" | "destructive" | "outline";
    text: string;
    class: string;
  } {
    switch (status) {
      case "active":
        return {
          variant: "default" as const,
          text: "Active",
          class:
            "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100",
        };
      case "stale":
        return {
          variant: "secondary" as const,
          text: "Stale",
          class:
            "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-100",
        };
      case "inactive":
        return {
          variant: "outline" as const,
          text: "Inactive",
          class: "",
        };
      default:
        return {
          variant: "secondary" as const,
          text: "Unknown",
          class: "",
        };
    }
  }

  function formatLastSeen(date?: Date): string {
    if (!date) return "Never";
    const d = new Date(date);
    const diff = Date.now() - d.getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 1) return "Just now";
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    return d.toLocaleDateString();
  }

  function getCategoryIcon(category: string | undefined) {
    switch (category) {
      case "cgm":
        return Activity;
      case "pump":
        return Database;
      case "aid-system":
        return Settings;
      case "connector":
        return Cloud;
      case "uploader":
        return Smartphone;
      case "demo":
        return Sparkles;
      default:
        return Plug;
    }
  }

  function getPlatformIcon(platform: string | undefined) {
    switch (platform) {
      case "ios":
        return Apple;
      case "android":
        return TabletSmartphone;
      default:
        return Smartphone;
    }
  }

  async function copyToClipboard(text: string, field: string) {
    await navigator.clipboard.writeText(text);
    copiedField = field;
    setTimeout(() => {
      copiedField = null;
    }, 2000);
  }
</script>

<svelte:head>
  <title>Services - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl space-y-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Data Sources & Services</h1>
      <p class="text-muted-foreground">
        See what's sending data to Nocturne and set up new connections
      </p>
    </div>
    <Button variant="outline" size="sm" onclick={loadServices} class="gap-2">
      <RefreshCw class="h-4 w-4 {isLoading ? 'animate-spin' : ''}" />
      Refresh
    </Button>
  </div>

  {#if isLoading && !servicesOverview}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-8">
        <div class="text-center">
          <AlertCircle class="h-12 w-12 mx-auto mb-4 text-destructive" />
          <p class="font-medium">Failed to load services</p>
          <p class="text-sm text-muted-foreground mt-1">{error}</p>
          <Button class="mt-4" onclick={loadServices}>Try Again</Button>
        </div>
      </CardContent>
    </Card>
  {:else if servicesOverview}
    <!-- Active Data Sources -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Wifi class="h-5 w-5" />
          Active Data Sources
        </CardTitle>
        <CardDescription>
          Devices and apps currently sending data to this Nocturne instance
        </CardDescription>
      </CardHeader>
      <CardContent>
        {#if !servicesOverview.activeDataSources || servicesOverview.activeDataSources.length === 0}
          <div class="text-center py-8 text-muted-foreground">
            <WifiOff class="h-12 w-12 mx-auto mb-4 opacity-50" />
            <p class="font-medium">No data sources detected</p>
            <p class="text-sm">
              Set up an uploader app to start sending data to Nocturne
            </p>
          </div>
        {:else}
          <div class="space-y-3">
            {#each servicesOverview.activeDataSources as source}
              {@const Icon = getCategoryIcon(source.category)}
              {@const matchingUploader = getMatchingUploader(source)}
              {@const isDemo = isDemoDataSource(source)}
              <button
                class="w-full flex items-center justify-between p-4 rounded-lg border bg-card hover:bg-accent/50 transition-colors text-left {isDemo
                  ? 'border-purple-200 dark:border-purple-800 bg-purple-50/50 dark:bg-purple-950/20'
                  : ''}"
                onclick={() => openDataSourceDialog(source)}
              >
                <div class="flex items-center gap-4">
                  <div
                    class="flex h-10 w-10 items-center justify-center rounded-lg {isDemo
                      ? 'bg-purple-100 dark:bg-purple-900/30'
                      : source.status === 'active'
                        ? 'bg-green-100 dark:bg-green-900/30'
                        : source.status === 'stale'
                          ? 'bg-yellow-100 dark:bg-yellow-900/30'
                          : 'bg-muted'}"
                  >
                    <Icon
                      class="h-5 w-5 {isDemo
                        ? 'text-purple-600 dark:text-purple-400'
                        : source.status === 'active'
                          ? 'text-green-600 dark:text-green-400'
                          : source.status === 'stale'
                            ? 'text-yellow-600 dark:text-yellow-400'
                            : 'text-muted-foreground'}"
                    />
                  </div>
                  <div>
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">
                        {source.name ?? "Unknown"}
                      </span>
                      {#if isDemo}
                        <Badge
                          variant="secondary"
                          class="bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-100"
                        >
                          <Sparkles class="h-3 w-3 mr-1" />
                          Demo
                        </Badge>
                      {:else}
                        <Badge
                          variant={getStatusBadge(source.status).variant}
                          class={getStatusBadge(source.status).class}
                        >
                          {#if source.status === "active"}
                            <CheckCircle class="h-3 w-3 mr-1" />
                          {:else if source.status === "stale"}
                            <Clock class="h-3 w-3 mr-1" />
                          {:else}
                            <AlertCircle class="h-3 w-3 mr-1" />
                          {/if}
                          {getStatusBadge(source.status).text}
                        </Badge>
                      {/if}
                      {#if matchingUploader}
                        <Badge variant="outline" class="text-xs">
                          {matchingUploader.name}
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground">
                      {source.description ?? source.deviceId}
                    </p>
                  </div>
                </div>
                <div class="flex items-center gap-4">
                  <div class="text-right text-sm">
                    <div class="flex items-center gap-1 text-muted-foreground">
                      <Clock class="h-3 w-3" />
                      {formatLastSeen(source.lastSeen)}
                    </div>
                    <div class="text-xs text-muted-foreground mt-1">
                      {source.entriesLast24h ?? 0} entries (24h)
                    </div>
                  </div>
                  <ChevronRight class="h-4 w-4 text-muted-foreground" />
                </div>
              </button>
            {/each}
          </div>
        {/if}
      </CardContent>
    </Card>

    <!-- Uploader Apps -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Smartphone class="h-5 w-5" />
          Set Up an Uploader
        </CardTitle>
        <CardDescription>
          Connect your CGM app or AID system to push data to Nocturne
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Tabs.Root value="cgm">
          <Tabs.List class="grid w-full grid-cols-3">
            <Tabs.Trigger value="cgm">CGM Apps</Tabs.Trigger>
            <Tabs.Trigger value="aid">AID Systems</Tabs.Trigger>
            <Tabs.Trigger value="other">Other</Tabs.Trigger>
          </Tabs.List>

          <Tabs.Content value="cgm" class="mt-4">
            <div class="grid gap-3 sm:grid-cols-2">
              {#each (servicesOverview.uploaderApps ?? []).filter((u: UploaderApp) => u.category === "cgm") as uploader}
                {@const PlatformIcon = getPlatformIcon(uploader.platform)}
                {@const active = isUploaderActive(uploader)}
                <button
                  class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                    ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                    : ''}"
                  onclick={() => openUploaderSetup(uploader)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                      ? 'bg-green-100 dark:bg-green-900/30'
                      : 'bg-primary/10'}"
                  >
                    <PlatformIcon
                      class="h-5 w-5 {active
                        ? 'text-green-600 dark:text-green-400'
                        : 'text-primary'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{uploader.name}</span>
                      <Badge variant="outline" class="text-xs capitalize">
                        {uploader.platform}
                      </Badge>
                      {#if active}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground truncate">
                      {uploader.description}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
              {/each}
            </div>
          </Tabs.Content>

          <Tabs.Content value="aid" class="mt-4">
            <div class="grid gap-3 sm:grid-cols-2">
              {#each (servicesOverview.uploaderApps ?? []).filter((u: UploaderApp) => u.category === "aid-system") as uploader}
                {@const PlatformIcon = getPlatformIcon(uploader.platform)}
                {@const active = isUploaderActive(uploader)}
                <button
                  class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                    ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                    : ''}"
                  onclick={() => openUploaderSetup(uploader)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                      ? 'bg-green-100 dark:bg-green-900/30'
                      : 'bg-primary/10'}"
                  >
                    <PlatformIcon
                      class="h-5 w-5 {active
                        ? 'text-green-600 dark:text-green-400'
                        : 'text-primary'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{uploader.name}</span>
                      <Badge variant="outline" class="text-xs capitalize">
                        {uploader.platform}
                      </Badge>
                      {#if active}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground truncate">
                      {uploader.description}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
              {/each}
            </div>
          </Tabs.Content>

          <Tabs.Content value="other" class="mt-4">
            <div class="grid gap-3 sm:grid-cols-2">
              {#each (servicesOverview.uploaderApps ?? []).filter((u: UploaderApp) => u.category !== "cgm" && u.category !== "aid-system") as uploader}
                {@const PlatformIcon = getPlatformIcon(uploader.platform)}
                {@const active = isUploaderActive(uploader)}
                <button
                  class="flex items-center gap-4 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors text-left group {active
                    ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                    : ''}"
                  onclick={() => openUploaderSetup(uploader)}
                >
                  <div
                    class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                      ? 'bg-green-100 dark:bg-green-900/30'
                      : 'bg-primary/10'}"
                  >
                    <PlatformIcon
                      class="h-5 w-5 {active
                        ? 'text-green-600 dark:text-green-400'
                        : 'text-primary'}"
                    />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                      <span class="font-medium">{uploader.name}</span>
                      <Badge variant="outline" class="text-xs capitalize">
                        {uploader.platform}
                      </Badge>
                      {#if active}
                        <Badge
                          class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                        >
                          <CheckCircle class="h-3 w-3 mr-1" />
                          Active
                        </Badge>
                      {/if}
                    </div>
                    <p class="text-sm text-muted-foreground truncate">
                      {uploader.description}
                    </p>
                  </div>
                  <ChevronRight
                    class="h-4 w-4 text-muted-foreground group-hover:text-foreground transition-colors"
                  />
                </button>
              {/each}
            </div>
          </Tabs.Content>
        </Tabs.Root>
      </CardContent>
    </Card>

    <!-- Server-Side Connectors -->
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Cloud class="h-5 w-5" />
          Server Connectors
        </CardTitle>
        <CardDescription>
          Connectors that run on the server to pull data from cloud services
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="grid gap-3 sm:grid-cols-2">
          {#each servicesOverview.availableConnectors ?? [] as connector}
            {@const Icon = getCategoryIcon(connector.category)}
            {@const active = isConnectorActive(connector)}
            <div
              class="flex items-center gap-4 p-4 rounded-lg border {active
                ? 'border-green-300 dark:border-green-700 bg-green-50/50 dark:bg-green-950/20'
                : 'bg-muted/30'}"
            >
              <div
                class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg {active
                  ? 'bg-green-100 dark:bg-green-900/30'
                  : 'bg-primary/10'}"
              >
                <Icon
                  class="h-5 w-5 {active
                    ? 'text-green-600 dark:text-green-400'
                    : 'text-primary'}"
                />
              </div>
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 flex-wrap">
                  <span class="font-medium">{connector.name}</span>
                  {#if active}
                    <Badge
                      class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100 text-xs"
                    >
                      <CheckCircle class="h-3 w-3 mr-1" />
                      Active
                    </Badge>
                  {:else if connector.requiresServerConfig}
                    <Badge variant="outline" class="text-xs">
                      Server Config
                    </Badge>
                  {/if}
                </div>
                <p class="text-sm text-muted-foreground">
                  {connector.description}
                </p>
              </div>
              {#if connector.documentationUrl}
                <Button variant="ghost" size="sm">
                  <a
                    href={connector.documentationUrl}
                    target="_blank"
                    rel="noopener"
                  >
                    <ExternalLink class="h-4 w-4" />
                  </a>
                </Button>
              {/if}
            </div>
          {/each}
        </div>
        <p class="text-sm text-muted-foreground mt-4">
          Server connectors require environment variable configuration. See the
          documentation for setup instructions.
        </p>
      </CardContent>
    </Card>

    <!-- API Info -->
    {#if servicesOverview.apiEndpoint}
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <Database class="h-5 w-5" />
            API Information
          </CardTitle>
          <CardDescription>
            Use these endpoints to configure uploaders manually
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-2">
              <span class="text-sm font-medium">Base URL</span>
              <div class="flex gap-2">
                <code
                  class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono truncate"
                >
                  {servicesOverview.apiEndpoint.baseUrl}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() =>
                    copyToClipboard(
                      servicesOverview!.apiEndpoint!.baseUrl!,
                      "baseUrl"
                    )}
                >
                  {#if copiedField === "baseUrl"}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
            </div>
            <div class="space-y-2">
              <span class="text-sm font-medium">Entries Endpoint</span>
              <div class="flex gap-2">
                <code
                  class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono truncate"
                >
                  {servicesOverview.apiEndpoint.entriesEndpoint}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() =>
                    copyToClipboard(
                      servicesOverview!.apiEndpoint!.entriesEndpoint!,
                      "entries"
                    )}
                >
                  {#if copiedField === "entries"}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
            </div>
          </div>
          <Separator />
          <p class="text-sm text-muted-foreground">
            Most uploaders use the Nightscout API format. Use your API secret
            for authentication via the <code class="text-xs">api-secret</code>
            header or embed it in the URL.
          </p>
        </CardContent>
      </Card>
    {/if}
  {/if}
</div>

<!-- Setup Instructions Dialog -->
<Dialog.Root bind:open={showSetupDialog}>
  <Dialog.Content class="max-w-2xl max-h-[80vh] overflow-y-auto">
    {#if selectedUploader && uploaderSetup}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          {@const PlatformIcon = getPlatformIcon(selectedUploader.platform)}
          <PlatformIcon class="h-5 w-5" />
          Set up {selectedUploader.name}
        </Dialog.Title>
        <Dialog.Description>
          {selectedUploader.description}
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-6 py-4">
        <!-- Connection Info -->
        <div class="space-y-3">
          <h4 class="font-medium">Connection Details</h4>

          <div class="space-y-2">
            <span class="text-sm text-muted-foreground">Nightscout URL</span>
            <div class="flex gap-2">
              <code
                class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono break-all"
              >
                {uploaderSetup.baseUrl}
              </code>
              <Button
                variant="outline"
                size="icon"
                onclick={() =>
                  copyToClipboard(uploaderSetup!.baseUrl!, "dialogUrl")}
              >
                {#if copiedField === "dialogUrl"}
                  <Check class="h-4 w-4 text-green-500" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>

          {#if selectedUploader.id === "xdrip"}
            <div class="space-y-2">
              <span class="text-sm text-muted-foreground">
                xDrip+ Style URL (with API secret)
              </span>
              <div class="flex gap-2">
                <code
                  class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono break-all"
                >
                  {uploaderSetup.xdripStyleUrl}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() =>
                    copyToClipboard(uploaderSetup!.xdripStyleUrl!, "xdripUrl")}
                >
                  {#if copiedField === "xdripUrl"}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
              <p class="text-xs text-muted-foreground">
                Replace YOUR-API-SECRET with your actual API secret
              </p>
            </div>
          {/if}

          <div class="space-y-2">
            <span class="text-sm text-muted-foreground">API Secret</span>
            <div class="flex gap-2">
              <code
                class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono"
              >
                {uploaderSetup.apiSecretPlaceholder}
              </code>
            </div>
            <p class="text-xs text-muted-foreground">
              Use the API secret you configured for your Nocturne instance
            </p>
          </div>
        </div>

        <Separator />

        <!-- Setup Steps -->
        {#if selectedUploader.setupInstructions && selectedUploader.setupInstructions.length > 0}
          <div class="space-y-4">
            <h4 class="font-medium">Setup Instructions</h4>

            <ol class="space-y-4">
              {#each selectedUploader.setupInstructions as step}
                <li class="flex gap-4">
                  <div
                    class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-medium"
                  >
                    {step.step}
                  </div>
                  <div class="flex-1 pt-1">
                    <p class="font-medium">{step.title}</p>
                    <p class="text-sm text-muted-foreground mt-1">
                      {step.description}
                    </p>
                  </div>
                </li>
              {/each}
            </ol>
          </div>
        {/if}

        {#if selectedUploader.url}
          <div class="pt-4">
            <Button variant="outline" class="w-full gap-2">
              <a
                href={selectedUploader.url}
                target="_blank"
                rel="noopener"
                class="flex items-center gap-2"
              >
                <ExternalLink class="h-4 w-4" />
                Visit {selectedUploader.name} Website
              </a>
            </Button>
          </div>
        {/if}
      </div>
    {:else}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Demo Data Management Dialog -->
<Dialog.Root bind:open={showDemoDataDialog}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Sparkles class="h-5 w-5 text-purple-500" />
        Demo Data
      </Dialog.Title>
      <Dialog.Description>
        Manage the simulated demo data in your Nocturne instance
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if demoDeleteResult}
        {#if demoDeleteResult.success}
          <div
            class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
          >
            <div
              class="flex items-center gap-2 text-green-800 dark:text-green-200"
            >
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">Demo data cleared successfully</span>
            </div>
            <p class="text-sm text-green-700 dark:text-green-300 mt-1">
              Deleted {demoDeleteResult.entriesDeleted?.toLocaleString() ?? 0} entries
            </p>
          </div>
        {:else}
          <div
            class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
          >
            <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Failed to delete demo data</span>
            </div>
            <p class="text-sm text-red-700 dark:text-red-300 mt-1">
              {demoDeleteResult.error}
            </p>
          </div>
        {/if}
      {:else}
        <div
          class="rounded-lg border border-purple-200 dark:border-purple-800 bg-purple-50 dark:bg-purple-950/20 p-4"
        >
          <p class="text-sm text-purple-800 dark:text-purple-200">
            <strong>This is demo data</strong>
            — synthetic glucose readings generated for testing and demonstration purposes.
          </p>
        </div>

        <div class="space-y-3">
          <p class="text-sm text-muted-foreground">
            You can safely delete all demo data. It's very easy to regenerate:
          </p>
          <ul
            class="text-sm text-muted-foreground list-disc list-inside space-y-1"
          >
            <li>Restart the demo service to regenerate data</li>
            <li>Only demo-generated data will be deleted</li>
            <li>Your real health data (if any) is not affected</li>
          </ul>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showDemoDataDialog = false)}>
        Close
      </Button>
      {#if !demoDeleteResult?.success}
        <Button
          variant="destructive"
          onclick={deleteDemoData}
          disabled={isDeletingDemo}
          class="gap-2"
        >
          {#if isDeletingDemo}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            Clear Demo Data
          {/if}
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Data Source Management Dialog -->
<Dialog.Root bind:open={showManageDataSourceDialog}>
  <Dialog.Content class="max-w-md">
    {#if selectedDataSource}
      {@const Icon = getCategoryIcon(selectedDataSource.category)}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <Icon class="h-5 w-5" />
          {selectedDataSource.name}
        </Dialog.Title>
        <Dialog.Description>
          {selectedDataSource.description ?? selectedDataSource.deviceId}
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-4 py-4">
        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <span class="text-muted-foreground">Status</span>
            <div class="mt-1">
              <Badge
                variant={getStatusBadge(selectedDataSource.status).variant}
                class={getStatusBadge(selectedDataSource.status).class}
              >
                {getStatusBadge(selectedDataSource.status).text}
              </Badge>
            </div>
          </div>
          <div>
            <span class="text-muted-foreground">Last Seen</span>
            <p class="mt-1 font-medium">
              {formatLastSeen(selectedDataSource.lastSeen)}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Entries (24h)</span>
            <p class="mt-1 font-medium">
              {selectedDataSource.entriesLast24h?.toLocaleString() ?? 0}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Total Entries</span>
            <p class="mt-1 font-medium">
              {selectedDataSource.totalEntries?.toLocaleString() ?? 0}
            </p>
          </div>
        </div>

        <Separator />

        <div
          class="rounded-lg border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-950/20 p-4"
        >
          <div class="flex items-start gap-3">
            <AlertTriangle
              class="h-5 w-5 text-amber-600 dark:text-amber-400 shrink-0 mt-0.5"
            />
            <div>
              <p class="text-sm font-medium text-amber-800 dark:text-amber-200">
                Delete All Data from This Source
              </p>
              <p class="text-sm text-amber-700 dark:text-amber-300 mt-1">
                This will permanently delete all entries, treatments, and device
                status records from this data source.
              </p>
            </div>
          </div>
        </div>
      </div>

      <Dialog.Footer>
        <Button
          variant="outline"
          onclick={() => (showManageDataSourceDialog = false)}
        >
          Cancel
        </Button>
        <Button
          variant="outline"
          class="gap-2"
          onclick={openDeleteConfirmation}
        >
          <Pencil class="h-4 w-4" />
          Delete Data...
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Delete Confirmation Dialog (Stern Warning) -->
<AlertDialog.Root bind:open={showDeleteConfirmDialog}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-2 text-destructive">
        <AlertTriangle class="h-5 w-5" />
        Permanently Delete Data
      </AlertDialog.Title>
      <AlertDialog.Description class="space-y-4">
        {#if selectedDataSource}
          <div
            class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4 mt-4"
          >
            <p class="text-sm font-semibold text-red-800 dark:text-red-200">
              ⚠️ THIS ACTION CANNOT BE UNDONE
            </p>
            <p class="text-sm text-red-700 dark:text-red-300 mt-2">
              You are about to permanently delete <strong>all data</strong>
              from
              <strong>{selectedDataSource.name}</strong>
              . This includes:
            </p>
            <ul
              class="text-sm text-red-700 dark:text-red-300 list-disc list-inside mt-2 space-y-1"
            >
              <li>
                All glucose entries ({selectedDataSource.totalEntries?.toLocaleString() ??
                  0} entries)
              </li>
              <li>All treatments entered by this device</li>
              <li>All device status records</li>
            </ul>
          </div>

          {#if deleteResult}
            {#if deleteResult.success}
              <div
                class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
              >
                <div
                  class="flex items-center gap-2 text-green-800 dark:text-green-200"
                >
                  <CheckCircle class="h-5 w-5" />
                  <span class="font-medium">Data deleted successfully</span>
                </div>
                <p class="text-sm text-green-700 dark:text-green-300 mt-1">
                  Deleted {deleteResult.entriesDeleted?.toLocaleString() ?? 0} entries
                </p>
              </div>
            {:else}
              <div
                class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
              >
                <div
                  class="flex items-center gap-2 text-red-800 dark:text-red-200"
                >
                  <AlertCircle class="h-5 w-5" />
                  <span class="font-medium">Failed to delete data</span>
                </div>
                <p class="text-sm text-red-700 dark:text-red-300 mt-1">
                  {deleteResult.error}
                </p>
              </div>
            {/if}
          {:else}
            <div class="space-y-2 mt-4">
              <label for="confirm-delete" class="text-sm font-medium">
                Type <strong>DELETE</strong>
                to confirm:
              </label>
              <input
                id="confirm-delete"
                type="text"
                bind:value={deleteConfirmText}
                class="w-full px-3 py-2 rounded-md border bg-background text-sm"
                placeholder="Type DELETE"
              />
            </div>
          {/if}
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={() => (showDeleteConfirmDialog = false)}>
        Cancel
      </AlertDialog.Cancel>
      {#if !deleteResult?.success}
        <Button
          variant="destructive"
          onclick={deleteDataSource}
          disabled={isDeletingDataSource || deleteConfirmText !== "DELETE"}
          class="gap-2"
        >
          {#if isDeletingDataSource}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            Delete All Data
          {/if}
        </Button>
      {/if}
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
