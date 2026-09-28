<script lang="ts">
  import { page } from "$app/state";
  import { replaceState } from "$app/navigation";
  import { Artwork } from "@nocturne/watercolour";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import X from "@lucide/svelte/icons/x";
  import { getMyTenants } from "$api/generated/myTenants.generated.remote";
  import { isWelcomeViewer, takeWelcome } from "./welcome";

  let shown = $state(false);

  // Stripped on sight, so a reload, a bookmark or the back button never greets
  // anyone twice.
  $effect(() => {
    const rest = takeWelcome(page.url);
    if (!rest) return;
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL minus one param, already resolved
    replaceState(rest, {});
    shown = isWelcomeViewer(page.data.effectivePermissions ?? []);
  });

  // A guest session is a member of no tenant, so it gets the nameless line.
  const tenantsQuery = $derived(shown ? getMyTenants() : undefined);
  const name = $derived(
    tenantsQuery?.current?.find((t) => t.slug === page.data.tenantSlug)
      ?.displayName
  );
</script>

{#if shown}
  <div class="px-3 pt-3 md:px-6 md:pt-6">
    <Card.Root
      role="status"
      data-testid="join-welcome"
      size="sm"
      class="relative"
    >
      <div class="flex items-center gap-4 pr-8">
        <Artwork
          artwork="connected-shores"
          palette="water"
          motion="auto"
          autoplay="once"
          class="aspect-[3/1] w-36 shrink-0 md:w-56"
        />
        <div class="min-w-0">
          <p class="font-medium">Welcome</p>
          <p class="text-sm text-muted-foreground">
            {#if name}
              You're now viewing {name}'s data.
            {:else}
              You're now viewing the data shared with you.
            {/if}
          </p>
        </div>
      </div>
      <Button
        variant="ghost"
        size="icon-sm"
        class="absolute top-2 right-2"
        aria-label="Dismiss"
        onclick={() => (shown = false)}
      >
        <X />
      </Button>
    </Card.Root>
  </div>
{/if}
