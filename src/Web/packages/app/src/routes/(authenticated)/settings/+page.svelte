<script lang="ts">
  import { page } from "$app/state";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Settings from "@lucide/svelte/icons/settings";
  import {
    helpLinks,
    visibleSettingsGroups,
    type SettingsLink,
  } from "#lib/navigation/settings-navigation.svelte.js";
  import type { PageData } from "./$types";

  const { data }: { data: PageData } = $props();

  const groups = $derived(
    visibleSettingsGroups({
      grantedScopes: page.data.effectivePermissions ?? [],
      isPlatformAdmin: data.isPlatformAdmin ?? false,
    })
  );
</script>

<svelte:head>
  <title>Settings - Nocturne</title>
</svelte:head>

{#snippet linkList(links: SettingsLink[])}
  <ul class="m-0 grid list-none gap-x-10 p-0 @md:grid-cols-2">
    {#each links as link (link.href)}
      <li class="border-b border-border">
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- link.href is a literal in-app path from settings-navigation -->
        <a href={link.href}
          class="group -mx-2 flex items-center gap-3 rounded-md px-2 py-3 transition-colors hover:bg-accent/50"
        >
          <link.icon class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          <div class="min-w-0 flex-1">
            <p class="font-medium">{link.title}</p>
            <p class="text-sm text-muted-foreground">{link.description}</p>
          </div>
          <ChevronRight
            class="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
            aria-hidden="true"
          />
        </a>
      </li>
    {/each}
  </ul>
{/snippet}

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-10">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <Settings class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Settings</h1>
      <p class="text-muted-foreground">
        Manage your account, this site, and how Nocturne works for you.
      </p>
    </div>
  </div>

  {#each groups as group (group.id)}
    <section class="space-y-2" aria-labelledby="settings-group-{group.id}">
      <div>
        <h2 id="settings-group-{group.id}" class="text-lg font-semibold">{group.title}</h2>
        <p class="text-sm text-muted-foreground">{group.description}</p>
      </div>
      {@render linkList(group.links)}
    </section>
  {/each}

  <section class="space-y-2" aria-labelledby="settings-group-help">
    <h2 id="settings-group-help" class="text-lg font-semibold">Help</h2>
    {@render linkList(helpLinks())}
  </section>
</div>
