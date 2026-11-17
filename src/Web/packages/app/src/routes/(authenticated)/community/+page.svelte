<script lang="ts">
  import { resolve } from "$app/paths";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import GithubIcon from "#lib/components/icons/GithubIcon.svelte";
  import HeartHandshake from "@lucide/svelte/icons/heart-handshake";
  import MessageCircle from "@lucide/svelte/icons/message-circle";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import Heart from "@lucide/svelte/icons/heart";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Languages from "@lucide/svelte/icons/languages";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import { docsUrl } from "#lib/docs-url.js";
  import {
    supportedLocales,
    getLanguageLabel,
    type SupportedLocale,
  } from "#lib/stores/appearance-store.svelte.js";

  const communityLinks = $derived([
    {
      name: "GitHub repository",
      description: "Source code, issues, and feature requests",
      icon: GithubIcon,
      href: "https://github.com/nightscout/nocturne",
      badge: "Open source",
    },
    {
      name: "Discord community",
      description: "Chat with developers and other users",
      icon: MessageCircle,
      href: "https://discord.gg/xWYz9fFWrj",
      badge: "Active",
    },
    {
      name: "Documentation",
      description: "Guides, tutorials, and API reference",
      icon: BookOpen,
      href: docsUrl("/docs", "community"),
    },
    {
      name: "Nightscout Foundation",
      description: "The organization behind Nightscout",
      icon: Heart,
      href: "https://www.nightscoutfoundation.org/",
      badge: "501(c)(3)",
    },
  ]);

  const targetLocales = supportedLocales.filter(
    (l): l is SupportedLocale => l !== "en",
  );
</script>

<svelte:head>
  <title>Community - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <HeartHandshake class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Community</h1>
      <p class="text-muted-foreground">
        Connect with the Nightscout community and help make Nocturne better
      </p>
    </div>
  </div>

  <Card.Root>
    <Card.Header>
      <Card.Title class="flex items-center gap-2">
        <HeartHandshake class="h-5 w-5" />
        Find us
      </Card.Title>
      <Card.Description>Nocturne is made by people with diabetes, in the open</Card.Description>
    </Card.Header>
    <Card.Content class="space-y-4">
      {#each communityLinks as link (link.name)}
        <a
          href={link.href}
          target="_blank"
          rel="external noopener noreferrer"
          class="flex items-center justify-between gap-3 p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors"
        >
          <div class="flex min-w-0 items-center gap-4">
            <div
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
            >
              <link.icon class="h-5 w-5 text-primary" />
            </div>
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                <span class="font-medium">{link.name}</span>
                {#if link.badge}
                  <Badge variant="secondary">
                    {link.badge}
                  </Badge>
                {/if}
              </div>
              <p class="text-sm text-muted-foreground">{link.description}</p>
            </div>
          </div>
          <ExternalLink class="h-4 w-4 shrink-0 text-muted-foreground" />
        </a>
      {/each}
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title class="flex items-center gap-2">
        <Languages class="h-5 w-5" />
        Translations
      </Card.Title>
      <Card.Description>
        Translate Nocturne into your language. Drafts are saved to your account.
        When you submit, your work goes to the Nocturne project as a pull request
        with your name on it.
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <div class="grid gap-3 @md:grid-cols-2 @3xl:grid-cols-3">
        {#each targetLocales as locale (locale)}
          <a href={resolve("/(authenticated)/community/translations/[locale]", { locale })} class="group">
            <Card.Root interactive>
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
    </Card.Content>
  </Card.Root>
</div>
