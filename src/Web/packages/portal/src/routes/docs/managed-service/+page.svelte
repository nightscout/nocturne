<script lang="ts">
  import { resolve } from "$app/paths";
  import { ArtworkHero } from "water-ui";
  import { DOCS_HERO } from "#lib/data/docs-artwork.js";
  import { Button } from "@nocturne/ui/ui/button";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import MapPin from "@lucide/svelte/icons/map-pin";
  import PikaPodsVoteCard from "#lib/components/PikaPodsVoteCard.svelte";
  import HostingPicker from "#lib/components/docs/hosting/HostingPicker.svelte";
  import { campaignUrl } from "#lib/analytics.js";

  let showListingRequirements = $state(false);

  function shuffle<T>(array: T[]): T[] {
    const shuffled = [...array];
    for (let i = shuffled.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [shuffled[i], shuffled[j]] = [shuffled[j], shuffled[i]];
    }
    return shuffled;
  }

  const managedProviders = shuffle([
    {
      name: "nocturne.run",
      logo: "/logos/nocturne-run.svg",
      url: "https://nocturne.run",
      location: "Germany",
      license: "Commercial",
      blurb:
        "The official managed Nocturne instance, run by the creator of the project. Automatic updates, daily backups, and zero maintenance. Just connect your CGM and go.",
    },
  ]);

  const hero = DOCS_HERO["managed-service"];
</script>

<div class="max-w-3xl">
  <ArtworkHero
    artwork={hero.artwork}
    palette={hero.palette}
    motion="auto"
    autoplay="once"
    size={400}
    wrap={hero.wrap ?? true}
  />
  <h1 class="text-4xl font-bold tracking-tight mb-4">Managed service</h1>
  <p class="text-lg text-muted-foreground mb-8">
    With a managed service, a provider runs Nocturne for you. You do not need a server,
    a domain or Docker. You sign up with the provider and get your own site at an address
    that they give you. For example, a site on nocturne.run is at
    <code>yourname.nocturne.run</code>.
  </p>

  <HostingPicker current="managed" />

  <h2 class="text-2xl font-bold mt-8 mb-4">Providers</h2>
  <p class="text-muted-foreground mb-4">
    These Nocturne-as-a-Service providers handle the infrastructure so you can focus on
    your diabetes management.
  </p>
  <ul class="not-prose m-0 mb-4 p-0 list-none border-t border-border">
    {#each managedProviders as provider (provider.url)}
      <li class="border-b border-border">
        <a
          href={campaignUrl(provider.url, "managed-instances")}
          target="_blank"
          rel="external noopener noreferrer"
          class="group flex items-start gap-4 py-5 no-underline text-inherit"
        >
          <img src={provider.logo} alt="" class="size-8 rounded-md object-contain shrink-0 mt-0.5" />
          <div class="flex-1">
            <h3 class="text-lg font-semibold text-foreground m-0 mb-1 group-hover:underline underline-offset-4">{provider.name}</h3>
            <p class="text-sm text-muted-foreground m-0 mb-2">{provider.blurb}</p>
            <p class="flex items-center gap-4 text-xs text-muted-foreground m-0">
              <span class="flex items-center gap-1">
                <MapPin class="size-3" aria-hidden="true" />
                {provider.location}
              </span>
              <span>{provider.license} license</span>
            </p>
          </div>
          <ExternalLink class="size-5 mt-1 text-muted-foreground group-hover:text-foreground transition-colors" aria-hidden="true" />
        </a>
      </li>
    {/each}
    <li class="border-b border-border"><PikaPodsVoteCard /></li>
  </ul>

  <Button
    variant="subtle"
    size="inline"
    onclick={() => (showListingRequirements = !showListingRequirements)}
  >
    <ChevronDown
      class="w-4 h-4 transition-transform {showListingRequirements
        ? 'rotate-180'
        : ''}"
    />
    Want to list your service here?
  </Button>
  {#if showListingRequirements}
    <div class="mt-3 text-sm text-muted-foreground">
      <p class="mb-3">
        We welcome Nocturne-as-a-Service providers. To request a listing, provide the following:
      </p>
      <ul class="list-disc list-inside space-y-1">
        <li>Server's physical location</li>
        <li>AGPL code repository link (if not commercially licensed)</li>
        <li>Link to homepage</li>
        <li>Blurb (50 words max)</li>
        <li>Email address for core maintainer</li>
      </ul>
      <p class="mt-3">
        Submit your request by opening an issue on the <a
          href="https://github.com/nightscout/nocturne/issues/new"
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary hover:underline">Nocturne GitHub repository</a
        >.
      </p>
    </div>
  {/if}

  <h2 class="text-2xl font-bold mt-12 mb-4">After you sign up</h2>
  <p class="text-muted-foreground mb-4">
    Open your site and connect a data source. The
    <a href={resolve("/docs/getting-started")} class="text-primary hover:underline">quick start</a>
    shows how. The rest of these docs apply to your site too, except the installation
    guides and the server configuration, which the provider looks after.
  </p>
</div>
