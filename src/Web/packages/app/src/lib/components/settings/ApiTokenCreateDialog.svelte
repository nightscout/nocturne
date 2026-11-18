<script lang="ts">
  import { untrack } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import TokenScopeSelector from "./TokenScopeSelector.svelte";
  import Copy from "@lucide/svelte/icons/copy";
  import Check from "@lucide/svelte/icons/check";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { create as createGrant } from "#lib/api/generated/directGrants.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";

  let {
    open = $bindable(false),
    prefillLabel = "",
    prefillScopes = [],
    baseUrl,
    onCreated,
    onCloseAutoFocus,
  }: {
    open?: boolean;
    prefillLabel?: string;
    prefillScopes?: string[];
    /** Shown beside the new token, since an app configured by hand takes both. */
    baseUrl?: string;
    onCreated?: (grantId: string | undefined) => void;
    /** Lets the opener choose what takes focus when the dialog closes. */
    onCloseAutoFocus?: (event: Event) => void;
  } = $props();

  let label = $state("");
  let scopes = $state<string[]>([]);
  let limitTo24Hours = $state(false);
  let isCreating = $state(false);
  let createdToken = $state<string | null>(null);
  let createError = $state<string | null>(null);
  const copy = createCopyFeedback();

  $effect(() => {
    if (!open) return;
    untrack(() => {
      label = prefillLabel;
      scopes = [...prefillScopes];
      limitTo24Hours = false;
      createdToken = null;
      createError = null;
    });
  });

  async function handleCreate() {
    isCreating = true;
    createError = null;

    try {
      const data = await createGrant({ label, scopes, limitTo24Hours });
      createdToken = data.token ?? null;
      onCreated?.(data.id);
    } catch (err) {
      createError = describeSubmitError(err, "Failed to create token.");
    } finally {
      isCreating = false;
    }
  }

  // The only close path. bits-ui reports its own dismissals (escape, overlay, the X) through
  // onOpenChange but says nothing about a programmatic one, so anything that closes this dialog
  // from script comes through here.
  function close() {
    open = false;
    createdToken = null;
  }
</script>

<Dialog.Root bind:open onOpenChange={(next) => !next && close()}>
  <Dialog.Content class="max-w-lg max-h-[90vh] overflow-y-auto" {onCloseAutoFocus}>
    {#if createdToken}
      <Dialog.Header>
        <Dialog.Title>Token created</Dialog.Title>
        <Dialog.Description>
          Copy this token now. It will not be shown again.
        </Dialog.Description>
      </Dialog.Header>
      <div class="space-y-4 py-4">
        <div
          class="flex items-start gap-3 rounded-md border border-warning/30 bg-warning/10 p-3"
        >
          <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-warning" />
          <p class="text-sm text-warning">
            This token will only be shown once. Copy it now.
          </p>
        </div>
        <div class="flex gap-2">
          <Input
            type="text"
            value={createdToken}
            readonly
            class="font-mono"
            aria-label="API token"
          />
          <Button
            variant="outline"
            size="icon"
            aria-label="Copy token"
            onclick={() => createdToken && copy.copy(createdToken)}
          >
            {#if copy.isCopied()}
              <Check class="h-4 w-4 text-success" />
            {:else}
              <Copy class="h-4 w-4" />
            {/if}
          </Button>
        </div>
        {#if baseUrl}
          <div class="space-y-2">
            <Label for="token-base-url">Base URL</Label>
            <div class="flex gap-2">
              <Input
                id="token-base-url"
                type="text"
                value={baseUrl}
                readonly
                class="font-mono"
              />
              <Button
                variant="outline"
                size="icon"
                aria-label="Copy base URL"
                onclick={() => copy.copy(baseUrl, "baseUrl")}
              >
                {#if copy.isCopied("baseUrl")}
                  <Check class="h-4 w-4 text-success" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>
        {/if}
      </div>
      <Dialog.Footer>
        <Button onclick={close}>Done</Button>
      </Dialog.Footer>
    {:else}
      <Dialog.Header>
        <Dialog.Title>Create API token</Dialog.Title>
        <Dialog.Description>
          Enter a label and select the permissions of this token.
        </Dialog.Description>
      </Dialog.Header>
      <div class="space-y-4 py-4">
        {#if createError}
          <div
            role="alert"
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{createError}</p>
          </div>
        {/if}

        <div class="space-y-2">
          <Label for="token-label">Label</Label>
          <Input
            id="token-label"
            type="text"
            placeholder="e.g. xDrip uploader, Home Assistant"
            bind:value={label}
          />
        </div>

        <div class="space-y-3">
          <Label>Permissions</Label>
          <TokenScopeSelector bind:selected={scopes} />
        </div>

        <div class="flex items-start gap-2 rounded-md border p-3 bg-muted/30">
          <Checkbox
            id="token-limit-24h"
            checked={limitTo24Hours}
            onCheckedChange={(checked: boolean) => {
              limitTo24Hours = checked === true;
            }}
          />
          <div class="flex-1">
            <label
              for="token-limit-24h"
              class="text-sm font-medium cursor-pointer select-none"
            >
              Only last 24 hours
            </label>
            <p class="text-xs text-muted-foreground mt-0.5">
              Restrict this token to only the most recent 24 hours of data.
            </p>
          </div>
        </div>
      </div>
      <Dialog.Footer>
        <Button variant="outline" onclick={close}>Cancel</Button>
        <Button
          disabled={!label.trim() || scopes.length === 0 || isCreating}
          onclick={handleCreate}
        >
          {#if isCreating}
            <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
          {/if}
          Create token
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
