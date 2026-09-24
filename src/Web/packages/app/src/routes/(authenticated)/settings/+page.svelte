<script lang="ts">
  import { ChevronRight, Settings } from "lucide-svelte";
  import {
    adminSettingsSections,
    onboardingSection,
    settingsSections,
    type SettingsLink,
  } from "$lib/components/settings/settings-links";
  import type { PageData } from "./$types";

  const { data }: { data: PageData } = $props();

  const isPlatformAdmin = $derived(data.isPlatformAdmin ?? false);
</script>

<svelte:head>
  <title>Settings - Nocturne</title>
</svelte:head>

{#snippet linkList(links: SettingsLink[])}
  <ul class="m-0 grid list-none gap-x-10 p-0 @md:grid-cols-2">
    {#each links as link (link.href)}
      <li class="border-b border-border">
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- link.href is a literal in-app path from settings-links.ts -->
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

  {@render linkList(settingsSections)}

  {#if isPlatformAdmin}
    <section class="space-y-2 pt-6">
      <h2 class="text-lg font-semibold">Platform administration</h2>
      {@render linkList(adminSettingsSections)}
    </section>
  {/if}

  <section class="space-y-2 pt-6">
    <h2 class="text-lg font-semibold">Onboarding</h2>
    {@render linkList([onboardingSection])}
  </section>
</div>
