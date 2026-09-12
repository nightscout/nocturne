<script lang="ts">
  import { KeyRound } from "lucide-svelte";
  import ActiveSessions from "$lib/components/settings/ActiveSessions.svelte";
  import ApiReference from "$lib/components/settings/ApiReference.svelte";
  import SettingsLinkCard from "$lib/components/settings/SettingsLinkCard.svelte";
  import {
    connectorsLink,
    sharingLink,
  } from "$lib/components/settings/settings-links";

  // Connected apps and guest links are granted and revoked on the pages below, and were rendered
  // here as well. One home each keeps this page from claiming to own a section it cannot change.
  const managedElsewhere = [connectorsLink, sharingLink];
</script>

<svelte:head>
  <title>Active Access - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center gap-3">
    <div
      class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
    >
      <KeyRound class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Active Access</h1>
      <p class="text-muted-foreground">
        The browsers signed in to this account, and where to find everything
        else that can reach your data.
      </p>
    </div>
  </div>

  <div class="space-y-10">
    <ActiveSessions />

    <div class="space-y-4">
      <div class="space-y-1">
        <h2 class="text-lg font-semibold tracking-tight">
          What else can reach your data
        </h2>
        <p class="text-sm text-muted-foreground">
          Each of these is granted, and taken away again, on its own page.
        </p>
      </div>
      <div class="space-y-3">
        {#each managedElsewhere as link (link.href)}
          <SettingsLinkCard {link} />
        {/each}
      </div>
    </div>

    <ApiReference />
  </div>
</div>
