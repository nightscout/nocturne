<script lang="ts">
  import { KeyRound } from "lucide-svelte";
  import ActiveSessions from "$lib/components/settings/ActiveSessions.svelte";
  import ConnectedApps from "$lib/components/settings/ConnectedApps.svelte";
  import GuestLinksSection from "$lib/components/members/GuestLinksSection.svelte";
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
        Everything currently able to access this account's data.
      </p>
    </div>
  </div>

  <div class="space-y-10">
    <ActiveSessions />
    <GuestLinksSection />
    <ConnectedApps />
  </div>
</div>
