<script lang="ts">
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import ListChecks from "@lucide/svelte/icons/list-checks";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import X from "@lucide/svelte/icons/x";
  import { Button } from "$lib/components/ui/button";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import {
    dismissSetupStrip,
    getSetupHub,
  } from "$api/generated/setupHubs.generated.remote";

  // The hub is the owner's, so nobody else is asked for it.
  const hubQuery = satisfiesScope(page.data.effectivePermissions ?? [], "*")
    ? getSetupHub()
    : null;
  const hub = $derived(hubQuery?.current);

  /** Hidden at once on dismissal, ahead of the refreshed hub that agrees. */
  let dismissedAt = $state<string | undefined>(undefined);
  const shown = $derived(!!hub?.showStrip && hub.revision !== dismissedAt);

  async function dismiss() {
    if (!hub?.revision) return;
    dismissedAt = hub.revision;
    try {
      await dismissSetupStrip({ revision: hub.revision });
    } catch {
      // Still dismissed for this visit; the server offers it again next time.
    }
  }
</script>

{#if shown && hub}
  <div
    class="flex items-center gap-3 rounded-lg border bg-card px-4 py-2.5 text-sm"
    data-testid="setup-strip"
  >
    <ListChecks class="h-4 w-4 shrink-0 text-muted-foreground" />
    <span class="text-muted-foreground">
      {hub.resolvedCount} of {hub.totalCount} set up
    </span>
    <span class="text-muted-foreground" aria-hidden="true">&middot;</span>
    <a
      href={resolve("/setup")}
      class="inline-flex items-center gap-1 font-medium text-primary hover:underline"
    >
      Continue
      <ArrowRight class="h-3.5 w-3.5" />
    </a>
    <Button
      variant="ghost"
      size="icon-sm"
      class="ml-auto"
      aria-label="Dismiss setup reminder"
      onclick={dismiss}
    >
      <X />
    </Button>
  </div>
{/if}
