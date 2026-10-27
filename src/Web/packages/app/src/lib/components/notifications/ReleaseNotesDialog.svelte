<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { getReleaseStatus } from "#lib/api/generated/releases.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import {
    AutoUpdateMode,
    type InAppNotificationDto,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { formatLongDate } from "#lib/utils/formatting.js";
  import InstallTiming from "./InstallTiming.svelte";

  interface Props {
    open: boolean;
    notification: InAppNotificationDto | null;
  }

  let { open = $bindable(), notification }: Props = $props();

  const statusQuery = $derived(open ? getReleaseStatus() : null);
  retainQuery(() => statusQuery);
  const status = $derived(statusQuery?.current ?? null);

  const isUpdate = $derived(notification?.type === "system.update_available");
  const version = $derived(notification?.metadata?.["Version"]?.toString() ?? "");

  // The notice names one version; show its notes only while the server still describes that
  // version, so an older notice does not show a newer release's notes.
  const release = $derived.by(() => {
    if (!status) return null;
    const candidate = isUpdate ? status.latest : status.current;
    return candidate && candidate.version === version ? candidate : null;
  });
  const installed = $derived(isUpdate && status?.currentVersion === version);
  const manual = $derived(
    status?.autoUpdate?.mode === AutoUpdateMode.NotDetected ||
      status?.autoUpdate?.mode === AutoUpdateMode.MonitorOnly
  );
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="flex max-h-[85vh] flex-col sm:max-w-2xl">
    <Dialog.Header>
      <Dialog.Title>
        {#if isUpdate}
          Nocturne {version} is available
        {:else}
          Nocturne is now on version {version}
        {/if}
      </Dialog.Title>
      <Dialog.Description>
        {#if release?.publishedAt}
          Released on {formatLongDate(release.publishedAt)}.
        {/if}
        {#if isUpdate && status && !installed}
          <InstallTiming
            mode={status.autoUpdate?.mode}
            nextCheckAt={status.autoUpdate?.nextCheckAt}
            checkIntervalSeconds={status.autoUpdate?.checkIntervalSeconds}
          />
        {:else if installed}
          This update is installed.
        {/if}
      </Dialog.Description>
    </Dialog.Header>

    {#if isUpdate && manual && !installed}
      <div class="rounded-md border bg-muted/50 p-3 text-sm">
        <p class="mb-2 text-muted-foreground">
          To install it, run this command in the folder that holds your compose file:
        </p>
        <code class="block font-mono text-xs">docker compose pull && docker compose up -d</code>
      </div>
    {/if}

    <div class="min-h-0 flex-1 overflow-y-auto">
      {#if statusQuery?.loading && !status}
        <div class="flex items-center gap-2 py-6 text-sm text-muted-foreground">
          <Loader2 class="h-4 w-4 animate-spin" />
          Loading the release notes
        </div>
      {:else if statusQuery?.error}
        <p class="py-6 text-sm text-muted-foreground">
          Could not load the release notes. Try again later.
        </p>
      {:else if release}
        <div class="prose prose-sm max-w-none dark:prose-invert">
          <!-- eslint-disable-next-line svelte/no-at-html-tags -- rendered by GitHub, sanitized by the API's GitHubReleaseClient -->
          {@html release.notesHtml}
        </div>
      {:else}
        <p class="py-6 text-sm text-muted-foreground">
          The release notes for this version are not available.
        </p>
      {/if}
    </div>

    <Dialog.Footer>
      {#if release?.url}
        <Button variant="outline" href={release.url} target="_blank" rel="noopener noreferrer">
          <ExternalLink class="h-4 w-4" />
          Open on GitHub
        </Button>
      {/if}
      <Button onclick={() => (open = false)}>Close</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
