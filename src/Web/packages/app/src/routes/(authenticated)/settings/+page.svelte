<script lang="ts">
  import { Settings } from "lucide-svelte";
  import SettingsLinkCard from "$lib/components/settings/SettingsLinkCard.svelte";
  import {
    adminSettingsSections,
    onboardingSection,
    settingsSections,
  } from "$lib/components/settings/settings-links";
  import type { PageData } from "./$types";

  const { data }: { data: PageData } = $props();

  const isPlatformAdmin = $derived(data.isPlatformAdmin ?? false);
</script>

<svelte:head>
  <title>Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-8">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <Settings class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Settings</h1>
      <p class="text-muted-foreground">
        Manage your account, data, and how Nocturne works for you.
      </p>
    </div>
  </div>

  <div class="grid gap-3 @md:grid-cols-2">
    {#each settingsSections as section (section.href)}
      <SettingsLinkCard link={section} />
    {/each}
  </div>

  {#if isPlatformAdmin}
    <div class="space-y-3">
      <h2 class="text-sm font-medium uppercase tracking-wider text-muted-foreground">
        Platform Administration
      </h2>
      <div class="grid gap-3 @md:grid-cols-2">
        {#each adminSettingsSections as section (section.href)}
          <SettingsLinkCard link={section} />
        {/each}
      </div>
    </div>
  {/if}

  <div class="space-y-3">
    <h2 class="text-sm font-medium uppercase tracking-wider text-muted-foreground">
      Onboarding
    </h2>
    <div class="grid gap-3 @md:grid-cols-2">
      <SettingsLinkCard link={onboardingSection} />
    </div>
  </div>
</div>

