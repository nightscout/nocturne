<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Loader2,
    Copy,
    Check,
    ExternalLink,
    Smartphone,
    Cloud,
    Monitor,
  } from "lucide-svelte";
  import Apple from "lucide-svelte/icons/apple";
  import TabletSmartphone from "lucide-svelte/icons/tablet-smartphone";
  import XdripQuickConnect from "$lib/components/XdripQuickConnect.svelte";
  import PreludeQuickConnect from "$lib/components/PreludeQuickConnect.svelte";
  import type {
    UploaderApp,
    UploaderSetupResponse,
  } from "$lib/api/generated/nocturne-api-client";
  import { getUploaderSetup } from "$api/generated/services.generated.remote";
  import { KeyRound } from "lucide-svelte";
  import {
    getUploaderName,
    getUploaderDescription,
  } from "$lib/utils/uploader-labels";

  let {
    open = $bindable(false),
    selectedUploader = null,
    onRequestApiKey,
  }: {
    open: boolean;
    selectedUploader: UploaderApp | null;
    onRequestApiKey?: (label: string, scopes: string[]) => void;
  } = $props();

  const uploaderSetupQuery = $derived(
    open && selectedUploader?.id ? getUploaderSetup(selectedUploader.id) : null,
  );
  const uploaderSetup = $derived<UploaderSetupResponse | null>(
    uploaderSetupQuery?.current ?? null,
  );
  let copiedField = $state<string | null>(null);

  const hasOAuthFlow = $derived(
    selectedUploader?.id === "xdrip" || selectedUploader?.id === "prelude",
  );

  function handleRequestApiKey() {
    if (!selectedUploader) return;
    onRequestApiKey?.(getUploaderName(selectedUploader), ["health.readwrite"]);
  }

  async function copyToClipboard(text: string, field: string) {
    await navigator.clipboard.writeText(text);
    copiedField = field;
    setTimeout(() => {
      copiedField = null;
    }, 2000);
  }

  function getPlatformIcon(platform?: string) {
    switch (platform?.toLowerCase()) {
      case "ios":
        return Apple;
      case "android":
        return Smartphone;
      case "web":
        return Cloud;
      case "desktop":
        return Monitor;
      default:
        return TabletSmartphone;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-2xl max-h-[80vh] overflow-y-auto">
    {#if selectedUploader && uploaderSetup}
      {@const PlatformIcon = getPlatformIcon(selectedUploader.platform)}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <PlatformIcon class="h-5 w-5" />
          Set up {getUploaderName(selectedUploader)}
        </Dialog.Title>
        <Dialog.Description>
          {getUploaderDescription(selectedUploader)}
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-6 py-4">
        {#if selectedUploader?.id === "xdrip" && typeof window !== "undefined"}
          <div class="border-b pb-4 mb-4">
            <XdripQuickConnect instanceUrl={window.location.origin} />
          </div>
        {:else if selectedUploader?.id === "prelude" && typeof window !== "undefined"}
          <div class="border-b pb-4 mb-4">
            <PreludeQuickConnect instanceUrl={window.location.origin} />
          </div>
        {/if}

        <!-- Connection Info -->
        <div class="space-y-3">
          <h4 class="font-medium">Connection Details</h4>

          <div class="space-y-2">
            <span class="text-sm text-muted-foreground">Nocturne URL</span>
            <div class="flex gap-2">
              <code
                class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono break-all"
              >
                {typeof window !== "undefined" ? window.location.origin : ""}
              </code>
              <Button
                variant="outline"
                size="icon"
                onclick={() =>
                  typeof window !== "undefined" &&
                  copyToClipboard(window.location.origin, "dialogUrl")}
              >
                {#if copiedField === "dialogUrl"}
                  <Check class="h-4 w-4 text-green-500" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>

          {#if !hasOAuthFlow}
          <div class="space-y-2">
            <span class="text-sm text-muted-foreground">API Key</span>
            <div>
              <Button variant="outline" size="sm" onclick={handleRequestApiKey}>
                <KeyRound class="mr-1.5 h-4 w-4" />
                Generate API key
              </Button>
              <p class="text-xs text-muted-foreground mt-1.5">
                Creates an API token pre-configured for {getUploaderName(selectedUploader)}
              </p>
            </div>
          </div>
          {/if}

        <Separator />

        {#if selectedUploader.url}
          <div class="pt-4">
            <Button variant="outline" class="w-full gap-2">
              <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- external uploader website URL from the API, not an internal app route -->
              <a href={selectedUploader.url} target="_blank" rel="noopener" class="flex items-center gap-2">
                <ExternalLink class="h-4 w-4" />
                Visit {getUploaderName(selectedUploader)} Website
              </a>
            </Button>
          </div>
        {/if}
        </div>
      </div>
    {:else}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>
