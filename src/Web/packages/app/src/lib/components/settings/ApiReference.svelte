<script lang="ts">
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import { Switch } from "$lib/components/ui/switch";
  import { BookOpen, ExternalLink } from "lucide-svelte";
  import {
    getTenantSettings,
    setPublicDocs,
  } from "$api/generated/tenantSettings.generated.remote";
  import { retainQuery } from "$lib/api/retain-query.svelte";

  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  const canManageSettings = $derived(
    effectivePermissions.includes("*") ||
      effectivePermissions.includes("tenant.settings"),
  );

  const settingsQuery = $derived(canManageSettings ? getTenantSettings() : null);
  retainQuery(() => settingsQuery);

  // Held only while a write is in flight; null = use server truth.
  let pending = $state<boolean | null>(null);
  let busy = $state(false);
  let errorMessage = $state<string | null>(null);

  const enabled = $derived(
    pending ?? settingsQuery?.current?.allowPublicDocs ?? false,
  );

  async function setEnabled(on: boolean) {
    busy = true;
    errorMessage = null;
    pending = on;
    try {
      await setPublicDocs({ enabled: on });
    } catch {
      errorMessage = on
        ? "Couldn't publish the API reference. Please try again."
        : "Couldn't hide the API reference. Please try again.";
    } finally {
      busy = false;
      pending = null;
    }
  }
</script>

{#if canManageSettings}
  <Card.Root>
    <div class="flex items-start gap-4 p-5 @md:p-6">
      <div
        class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl {enabled
          ? 'bg-primary/10 text-primary'
          : 'bg-muted text-muted-foreground'}"
      >
        <BookOpen class="h-5 w-5" />
      </div>
      <div class="min-w-0 flex-1">
        <h2 class="text-lg font-semibold">API reference</h2>
        <p class="mt-0.5 max-w-prose text-sm text-muted-foreground">
          Publishes the interactive API reference and its OpenAPI specification
          on this address, for anyone who visits — no sign-in required. Turn it
          on if you're building against the API; leave it off otherwise. Your
          data stays behind the same sign-in either way.
        </p>
      </div>
      <Switch
        checked={enabled}
        disabled={busy}
        onCheckedChange={(v: boolean) => setEnabled(v)}
        aria-label="API reference"
      />
    </div>

    {#if errorMessage}
      <div
        class="mx-5 mb-5 rounded-md border border-destructive/20 bg-destructive/5 p-3 @md:mx-6"
      >
        <p class="text-sm text-destructive">{errorMessage}</p>
      </div>
    {:else if enabled}
      <div class="border-t border-border px-5 py-4 @md:px-6">
        <a
          href="/scalar"
          class="inline-flex items-center gap-1.5 text-sm font-medium text-primary hover:underline"
        >
          Open the API reference
          <ExternalLink class="h-3.5 w-3.5" />
        </a>
      </div>
    {/if}
  </Card.Root>
{/if}
