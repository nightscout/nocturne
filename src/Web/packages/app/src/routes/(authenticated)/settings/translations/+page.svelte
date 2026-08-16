<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Languages, ChevronRight } from "@lucide/svelte";
  import {
    supportedLocales,
    getLanguageLabel,
    type SupportedLocale,
  } from "$lib/stores/appearance-store.svelte";

  const targetLocales = supportedLocales.filter(
    (l): l is SupportedLocale => l !== "en",
  );
</script>

<svelte:head>
  <title>Translations - Settings</title>
</svelte:head>

<div class="space-y-6">
  <div>
    <h1 class="flex items-center gap-3 text-2xl font-bold">
      <Languages class="h-6 w-6" />
      Translations
    </h1>
    <p class="mt-1 text-muted-foreground">
      Translate Nocturne into your language. Drafts are saved to your account,
      and submissions are proposed to the Nocturne project as a pull request
      with your name attached.
    </p>
  </div>

  <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
    {#each targetLocales as locale (locale)}
      <a href="/settings/translations/{locale}" class="group">
        <Card.Root class="transition-colors group-hover:border-primary/50">
          <Card.Content class="flex items-center justify-between p-4">
            <div>
              <p class="font-medium">{getLanguageLabel(locale, locale)}</p>
              <p class="text-sm text-muted-foreground">
                {getLanguageLabel(locale)}
              </p>
            </div>
            <div class="flex items-center gap-2">
              <Badge variant="outline">{locale}</Badge>
              <ChevronRight
                class="h-4 w-4 text-muted-foreground transition-transform group-hover:translate-x-0.5"
              />
            </div>
          </Card.Content>
        </Card.Root>
      </a>
    {/each}
  </div>
</div>
