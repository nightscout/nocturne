<script lang="ts">
  import {
    UploaderPlatform,
    type AvailableConnector,
    type GoogleHealthStatus,
    type UploaderApp,
  } from "@nocturne/api-client";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Item } from "#lib/components/ui/item/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import AppLogo from "#lib/components/ui/AppLogo.svelte";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Check from "@lucide/svelte/icons/check";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import Cloud from "@lucide/svelte/icons/cloud";
  import Copy from "@lucide/svelte/icons/copy";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import TabletSmartphone from "@lucide/svelte/icons/tablet-smartphone";
  import { resolve } from "$app/paths";
  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";
  import {
    getUploaderDescription,
    getUploaderName,
  } from "#lib/utils/uploader-labels.js";

  let {
    open = $bindable(false),
    canAddCloudAccount,
    uploaderApps,
    connectors,
    listFailed = false,
    googleHealth,
    baseUrl,
    onSelectUploader,
    onCreateToken,
  }: {
    open: boolean;
    /** Without it the dialog is the app branch alone, with no first question. */
    canAddCloudAccount: boolean;
    /** Undefined while the list is loading. */
    uploaderApps: UploaderApp[] | undefined;
    /** Connectors with no configuration yet. Undefined while the list is loading. */
    connectors: AvailableConnector[] | undefined;
    /** The lists could not be loaded, so neither is loading nor empty. */
    listFailed?: boolean;
    /** The Google Health entry, or null when the list does not offer it. */
    googleHealth: {
      status: GoogleHealthStatus | null;
      loading: boolean;
      failed: boolean;
    } | null;
    /** The address an app configured by hand sends to. */
    baseUrl: string | undefined;
    onSelectUploader: (uploader: UploaderApp) => void;
    onCreateToken: () => void;
  } = $props();

  type Step = "choose" | "app" | "cloud";

  let chosen = $state<Step>("choose");
  const step = $derived<Step>(canAddCloudAccount ? chosen : "app");
  let search = $state("");
  const copy = createCopyFeedback();

  // bits-ui reports its own dismissals through onOpenChange but not a close from script, so the
  // two handlers that close the dialog reset it themselves.
  function reset() {
    chosen = "choose";
    search = "";
  }

  function go(next: Step) {
    chosen = next;
    search = "";
  }

  const platformLabels: Record<UploaderPlatform, string> = {
    [UploaderPlatform.Android]: "Android",
    [UploaderPlatform.IOS]: "iOS",
    [UploaderPlatform.Desktop]: "Desktop",
    [UploaderPlatform.Web]: "Web",
  };

  const term = $derived(search.trim().toLowerCase());
  const visibleApps = $derived(
    (uploaderApps ?? []).filter(
      (app) => !term || getUploaderName(app).toLowerCase().includes(term)
    )
  );
  const visibleConnectors = $derived(
    (connectors ?? []).filter(
      (connector) =>
        !term || (connector.name ?? "").toLowerCase().includes(term)
    )
  );
  const googleHealthVisible = $derived(
    googleHealth !== null && (!term || "google health".includes(term))
  );
  const googleHealthUnavailable = $derived(
    !!googleHealth &&
      !googleHealth.failed &&
      !googleHealth.loading &&
      googleHealth.status?.available === false
  );
</script>

{#snippet loadingRows()}
  <div class="space-y-3 py-3" aria-busy="true">
    {#each [0, 1, 2] as i (i)}
      <div class="flex items-center gap-3">
        <Skeleton class="size-8 rounded-md" />
        <div class="flex-1 space-y-2">
          <Skeleton class="h-4 w-32" />
          <Skeleton class="h-3 w-48 max-w-full" />
        </div>
      </div>
    {/each}
  </div>
{/snippet}

{#snippet loadFailure()}
  <p role="alert" class="flex items-start gap-2 py-3 text-sm text-destructive">
    <CircleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
    Could not load this list. Close this window, then refresh the page.
  </p>
{/snippet}

{#snippet chevron()}
  <ChevronRight
    class="size-4 shrink-0 text-muted-foreground"
    aria-hidden="true"
  />
{/snippet}

{#snippet listControls(searchLabel: string)}
  <div class="flex flex-col gap-3">
    {#if canAddCloudAccount}
      <div>
        <Button variant="ghost" size="sm" onclick={() => go("choose")}>
          <ArrowLeft class="size-4" aria-hidden="true" />
          Back
        </Button>
      </div>
    {/if}
    <Input
      type="search"
      placeholder={searchLabel}
      aria-label={searchLabel}
      bind:value={search}
    />
  </div>
{/snippet}

{#snippet googleHealthRow(entry: NonNullable<typeof googleHealth>)}
  {#if googleHealthUnavailable}
    <li>
      <Item variant="row">
        <span
          class="flex size-8 shrink-0 items-center justify-center"
          aria-hidden="true"
        >
          <AppLogo
            icon="google-health"
            invertMode
            class="size-8 object-contain"
          />
        </span>
        <span class="min-w-0 flex-1">
          <span class="block font-medium text-muted-foreground">
            Google Health
          </span>
          <span class="block text-sm text-muted-foreground">
            Not enabled on this Nocturne server
          </span>
        </span>
      </Item>
    </li>
  {:else}
    <li>
      <Item variant="row" href={resolve("settings/connectors/google-health")}>
        <span
          class="flex size-8 shrink-0 items-center justify-center"
          aria-hidden="true"
        >
          <AppLogo
            icon="google-health"
            invertMode
            class="size-8 object-contain"
          />
        </span>
        <span class="min-w-0 flex-1">
          <span class="block font-medium">Google Health</span>
          <span class="block text-sm text-muted-foreground">
            Import steps, heart rate, weight, and sleep from Google Health
          </span>
        </span>
        <span class="shrink-0 text-sm text-muted-foreground">
          {#if entry.failed}
            Status unavailable
          {:else if entry.loading}
            Loading...
          {:else}
            Not connected
          {/if}
        </span>
        {@render chevron()}
      </Item>
    </li>
  {/if}
{/snippet}

<Dialog.Root bind:open onOpenChange={(next) => !next && reset()}>
  <Dialog.Content class="flex max-h-[85vh] max-w-lg flex-col">
    <Dialog.Header>
      <Dialog.Title>Add a connection</Dialog.Title>
      <Dialog.Description>
        {#if step === "choose"}
          Where is the data now?
        {:else if step === "app"}
          Select the app that will send data to Nocturne.
        {:else}
          Select the service that Nocturne will fetch data from.
        {/if}
      </Dialog.Description>
    </Dialog.Header>

    {#if step === "choose"}
      <ul class="divide-y border-y">
        <li>
          <Item variant="row" onclick={() => go("app")}>
            <Smartphone
              class="size-5 shrink-0 text-muted-foreground"
              aria-hidden="true"
            />
            <span class="min-w-0 flex-1">
              <span class="block font-medium">
                An app on your phone or computer
              </span>
              <span class="block text-sm text-muted-foreground">
                The app sends data to Nocturne.
              </span>
            </span>
            {@render chevron()}
          </Item>
        </li>
        <li>
          <Item variant="row" onclick={() => go("cloud")}>
            <Cloud
              class="size-5 shrink-0 text-muted-foreground"
              aria-hidden="true"
            />
            <span class="min-w-0 flex-1">
              <span class="block font-medium">A cloud account</span>
              <span class="block text-sm text-muted-foreground">
                Nocturne fetches data from the service.
              </span>
            </span>
            {@render chevron()}
          </Item>
        </li>
      </ul>
    {:else if step === "app"}
      {@render listControls("Search apps")}

      <div class="-mx-6 min-h-0 flex-1 space-y-4 overflow-y-auto px-6">
        {#if listFailed}
          {@render loadFailure()}
        {:else if uploaderApps === undefined}
          {@render loadingRows()}
        {:else if visibleApps.length === 0}
          <p class="py-3 text-sm text-muted-foreground">No apps match.</p>
        {:else}
          <ul class="divide-y border-y">
            {#each visibleApps as uploader (uploader.id)}
              <li>
                <Item
                  variant="row"
                  onclick={() => {
                    reset();
                    onSelectUploader(uploader);
                  }}
                >
                  <span class="flex size-8 shrink-0 items-center justify-center">
                    <AppLogo
                      icon={uploader.icon}
                      invertMode
                      class="size-8 object-contain"
                    />
                  </span>
                  <span class="min-w-0 flex-1">
                    <span class="block font-medium">
                      {getUploaderName(uploader)}
                    </span>
                    <span class="block truncate text-sm text-muted-foreground">
                      {getUploaderDescription(uploader)}
                    </span>
                  </span>
                  {#if uploader.platform}
                    <span class="shrink-0 text-sm text-muted-foreground">
                      {platformLabels[uploader.platform]}
                    </span>
                  {/if}
                  {@render chevron()}
                </Item>
              </li>
            {/each}
          </ul>
        {/if}

        <section class="space-y-2">
          <h3 class="text-sm font-semibold">Set up manually</h3>
          <ul class="divide-y border-y">
            <li>
              <Item
                variant="row"
                onclick={() => {
                  reset();
                  onCreateToken();
                }}
              >
                <KeyRound
                  class="size-5 shrink-0 text-muted-foreground"
                  aria-hidden="true"
                />
                <span class="min-w-0 flex-1">
                  <span class="block font-medium">Create API token</span>
                  <span class="block text-sm text-muted-foreground">
                    An app uses the token to send data or read data.
                  </span>
                </span>
                {@render chevron()}
              </Item>
            </li>
            <li>
              <Item variant="row" href={resolve("oauth/device")}>
                <TabletSmartphone
                  class="size-5 shrink-0 text-muted-foreground"
                  aria-hidden="true"
                />
                <span class="min-w-0 flex-1">
                  <span class="block font-medium">Enter a device code</span>
                  <span class="block text-sm text-muted-foreground">
                    Authorize an app that shows a code.
                  </span>
                </span>
                {@render chevron()}
              </Item>
            </li>
          </ul>

          {#if baseUrl}
            <div class="space-y-2 pt-2">
              <h4 class="text-sm font-medium">API information</h4>
              <p class="text-sm text-muted-foreground">
                Enter this base URL in an app that you configure manually.
              </p>
              <div class="flex gap-2">
                <code
                  class="flex-1 truncate rounded-md bg-muted px-3 py-2 font-mono text-sm"
                >
                  {baseUrl}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  aria-label="Copy base URL"
                  onclick={() => copy.copy(baseUrl, "baseUrl")}
                >
                  {#if copy.isCopied("baseUrl")}
                    <Check class="size-4 text-success" aria-hidden="true" />
                  {:else}
                    <Copy class="size-4" aria-hidden="true" />
                  {/if}
                </Button>
              </div>
            </div>
          {/if}
        </section>
      </div>
    {:else}
      {@render listControls("Search services")}

      <div class="-mx-6 min-h-0 flex-1 overflow-y-auto px-6">
        {#if listFailed}
          {@render loadFailure()}
        {:else if connectors === undefined}
          {@render loadingRows()}
        {:else if visibleConnectors.length === 0 && !googleHealthVisible}
          <p class="py-3 text-sm text-muted-foreground">
            {#if term}
              No services match.
            {:else}
              All available connectors are configured.
            {/if}
          </p>
        {:else}
          <ul class="divide-y border-y">
            {#if googleHealthVisible && googleHealth && !googleHealthUnavailable}
              {@render googleHealthRow(googleHealth)}
            {/if}
            {#each visibleConnectors as connector (connector.id)}
              <li>
                <Item
                  variant="row"
                  href={resolve(
                    `settings/connectors/${connector.id?.toLowerCase()}`
                  )}
                >
                  <span class="flex size-8 shrink-0 items-center justify-center">
                    <AppLogo
                      icon={connector.icon}
                      invertMode
                      class="size-8 object-contain"
                    />
                  </span>
                  <span class="min-w-0 flex-1">
                    <span class="block font-medium">{connector.name}</span>
                    <span class="block text-sm text-muted-foreground">
                      {connector.description}
                    </span>
                  </span>
                  {@render chevron()}
                </Item>
                <!-- A sibling of the row's link: an anchor inside an anchor is invalid. -->
                {#if connector.documentationUrl}
                  <p class="pb-3 pl-14 text-sm">
                    <a
                      href={connector.documentationUrl}
                      target="_blank"
                      rel="external noopener"
                      class="text-muted-foreground underline underline-offset-4 hover:text-foreground"
                    >
                      {connector.name} documentation
                    </a>
                  </p>
                {/if}
              </li>
            {/each}
            {#if googleHealthVisible && googleHealth && googleHealthUnavailable}
              {@render googleHealthRow(googleHealth)}
            {/if}
          </ul>
        {/if}
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>
