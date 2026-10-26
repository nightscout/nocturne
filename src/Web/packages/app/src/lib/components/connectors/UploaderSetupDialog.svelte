<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import Copy from "@lucide/svelte/icons/copy";
  import Check from "@lucide/svelte/icons/check";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Cloud from "@lucide/svelte/icons/cloud";
  import Monitor from "@lucide/svelte/icons/monitor";
  import Apple from "@lucide/svelte/icons/apple";
  import TabletSmartphone from "@lucide/svelte/icons/tablet-smartphone";
  import XdripQuickConnect from "#lib/components/XdripQuickConnect.svelte";
  import PreludeQuickConnect from "#lib/components/PreludeQuickConnect.svelte";
  import type { UploaderApp } from "#lib/api/generated/nocturne-api-client.js";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { create as createGrant } from "#lib/api/generated/directGrants.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";
  import {
    getUploaderName,
    getUploaderDescription,
  } from "#lib/utils/uploader-labels.js";

  let {
    open = $bindable(false),
    selectedUploader = null,
  }: {
    open: boolean;
    selectedUploader: UploaderApp | null;
  } = $props();

  const copy = createCopyFeedback();

  const hasOAuthFlow = $derived(
    selectedUploader?.id === "xdrip" || selectedUploader?.id === "prelude",
  );

  let apiToken = $state<string | null>(null);
  let apiTokenFor = $state<string | null>(null);
  let apiTokenLoading = $state(false);
  let apiTokenError = $state<string | null>(null);

  // A key belongs to the uploader it was made for; switching uploaders starts over.
  $effect(() => {
    if (selectedUploader?.id !== apiTokenFor) {
      apiToken = null;
      apiTokenError = null;
    }
  });

  async function generateApiToken() {
    if (!selectedUploader || apiTokenLoading) return;
    apiTokenLoading = true;
    apiTokenError = null;
    try {
      const result = await createGrant({
        label: getUploaderName(selectedUploader),
        scopes: ["health.readwrite"],
      });
      apiToken = result.token ?? null;
      apiTokenFor = selectedUploader.id ?? null;
    } catch (err) {
      apiTokenError = describeSubmitError(
        err,
        "Failed to generate API key. You can create one in the API tokens section.",
      );
    } finally {
      apiTokenLoading = false;
    }
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
  <Dialog.Content class="sm:max-w-2xl max-h-[80vh] overflow-y-auto">
    {#if selectedUploader}
      {@const PlatformIcon = getPlatformIcon(selectedUploader.platform)}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <PlatformIcon class="h-5 w-5" />
          Configure {getUploaderName(selectedUploader)}
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
          <h4 class="font-medium">Connection details</h4>

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
                  copy.copy(window.location.origin, "dialogUrl")}
              >
                {#if copy.isCopied("dialogUrl")}
                  <Check class="h-4 w-4 text-success" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>

          {#if !hasOAuthFlow}
            <div class="space-y-2">
              <span class="text-sm text-muted-foreground">API key</span>
              {#if apiToken}
                <div class="flex gap-2">
                  <code
                    class="flex-1 px-3 py-2 rounded-md bg-muted text-sm font-mono break-all"
                  >
                    {apiToken}
                  </code>
                  <Button
                    variant="outline"
                    size="icon"
                    aria-label="Copy API key"
                    onclick={() => apiToken && copy.copy(apiToken, "dialogToken")}
                  >
                    {#if copy.isCopied("dialogToken")}
                      <Check class="h-4 w-4 text-success" />
                    {:else}
                      <Copy class="h-4 w-4" />
                    {/if}
                  </Button>
                </div>
                <p class="text-xs text-muted-foreground">
                  Copy this now. It cannot be shown again.
                </p>
              {:else}
                <div>
                  <Button
                    variant="outline"
                    size="sm"
                    onclick={generateApiToken}
                    disabled={apiTokenLoading}
                  >
                    {#if apiTokenLoading}
                      <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
                    {:else}
                      <KeyRound class="mr-1.5 h-4 w-4" />
                    {/if}
                    Generate API key
                  </Button>
                  <p class="text-xs text-muted-foreground mt-1.5">
                    Creates an API key for {getUploaderName(selectedUploader)} with read and write access to health data.
                  </p>
                </div>
                {#if apiTokenError}
                  <p class="text-sm text-destructive">{apiTokenError}</p>
                {/if}
              {/if}
            </div>

            {#if selectedUploader.id === "aaps"}
              <p class="text-sm text-muted-foreground">
                In AAPS, open Config Builder, then Synchronization, and select NSClient V3. In its
                settings, paste the URL into Nightscout URL and the API key into NS access token.
              </p>
            {/if}
          {/if}

        <Separator />

        {#if selectedUploader.url}
          <div class="pt-4">
            <Button
              variant="outline"
              class="w-full"
              href={selectedUploader.url}
              target="_blank"
              rel="noopener"
            >
              <ExternalLink class="h-4 w-4" />
              Visit {getUploaderName(selectedUploader)} website
            </Button>
          </div>
        {/if}
        </div>
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>
