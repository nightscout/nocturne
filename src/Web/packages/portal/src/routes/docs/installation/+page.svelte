<script lang="ts">
  import { resolve } from "$app/paths";
  import { ArtworkHero } from "@nocturne/watercolour";
  import { DOCS_HERO } from "$lib/data/docs-artwork";
  import { Button } from "@nocturne/ui/ui/button";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import MapPin from "@lucide/svelte/icons/map-pin";
  import SystemRequirements from "$lib/components/docs/SystemRequirements.svelte";
  import PikaPodsVoteCard from "$lib/components/PikaPodsVoteCard.svelte";
  import { campaignUrl, track } from "$lib/analytics";

  let showListingRequirements = $state(false);

  function shuffle<T>(array: T[]): T[] {
    const shuffled = [...array];
    for (let i = shuffled.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [shuffled[i], shuffled[j]] = [shuffled[j], shuffled[i]];
    }
    return shuffled;
  }

  const PLATFORMS = [
    {
      id: "docker-compose",
      href: resolve("/docs/installation/docker-compose"),
      logo: "/logos/docker-compose.png",
      title: "Docker Compose",
      desc: "Deploy directly on any Linux server, VPS, or Raspberry Pi using Docker Compose from the command line.",
    },
    {
      id: "portainer",
      href: resolve("/docs/installation/portainer"),
      logo: "/logos/portainer.jpg",
      title: "Portainer",
      desc: "Deploy using the Portainer web interface. Great for managing your stack visually without SSH access.",
    },
    {
      id: "oracle-cloud",
      href: resolve("/docs/installation/oracle-cloud"),
      logo: "/logos/oracle.svg",
      title: "Oracle Cloud",
      desc: "One command in the browser sets up a free server, HTTPS and Nocturne. No SSH or Docker knowledge needed.",
    },
    {
      id: "byo-postgres",
      href: resolve("/docs/installation/byo-postgres"),
      logo: "/logos/postgresql.svg",
      title: "Bring your own PostgreSQL",
      desc: "Use a managed PostgreSQL service (RDS, Cloud SQL, Supabase, Neon) or an existing shared database instance. Requires a one-time role bootstrap.",
    },
    {
      id: "reverse-proxy",
      href: resolve("/docs/installation/reverse-proxy"),
      logo: "/logos/nginx.svg",
      title: "Bring your own reverse proxy",
      desc: "Terminate TLS with nginx, Traefik, or an existing edge instead of the bundled Caddy. Covers the forwarded headers Nocturne requires.",
    },
  ];

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

    const hero = DOCS_HERO["installation"];
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
  <h1 class="text-4xl font-bold tracking-tight mb-4">Installation guide</h1>
  <p class="text-lg text-muted-foreground mb-8">
    Choose a deployment method below to get Nocturne running on your
    infrastructure. All methods use the same Docker images and configuration.
  </p>

  <h2 class="text-2xl font-bold mt-8 mb-4">System requirements</h2>
  <SystemRequirements />

  <h2 class="text-2xl font-bold mt-8 mb-4">Choose your platform</h2>
  <ul class="not-prose m-0 p-0 list-none border-t border-border">
    {#each PLATFORMS as platform (platform.title)}
      <li class="border-b border-border">
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- platform.href is resolve()d in PLATFORMS -->
        <a href={platform.href} onclick={() => track("Install Platform", { platform: platform.id })} class="group flex items-start gap-4 py-5 no-underline text-inherit">
          <img src={platform.logo} alt="" class="size-8 rounded-md object-contain shrink-0 mt-0.5" />
          <div class="flex-1">
            <h3 class="text-lg font-semibold text-foreground m-0 mb-1 group-hover:underline underline-offset-4">{platform.title}</h3>
            <p class="text-sm text-muted-foreground m-0">{platform.desc}</p>
          </div>
          <ArrowRight class="size-5 mt-1 text-muted-foreground group-hover:text-foreground transition-colors" aria-hidden="true" />
        </a>
      </li>
    {/each}
  </ul>
  <p class="text-sm text-muted-foreground mt-4 mb-0">
    Guides for GCP, Azure, Heroku, and other cloud platforms are coming soon.
  </p>

  <h2 class="text-2xl font-bold mt-12 mb-4">Managed instances</h2>
  <p class="text-muted-foreground mb-4">
    Prefer not to self-host? These Nocturne-as-a-Service providers handle the
    infrastructure so you can focus on your diabetes management.
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

  <h2 class="text-2xl font-bold mt-12 mb-4">Platform notes</h2>

  <h3 class="text-xl font-semibold mt-6 mb-3">Linux (Ubuntu/Debian)</h3>
  <p class="text-muted-foreground mb-4">
    Install Docker using the official repository for the latest version. Both
    x86_64 and ARM64 architectures are supported.
  </p>

  <h3 class="text-xl font-semibold mt-6 mb-3">Raspberry Pi</h3>
  <p class="text-muted-foreground mb-4">
    Nocturne supports ARM64 architecture. Use Raspberry Pi 4 or newer with
    64-bit Raspberry Pi OS.
  </p>

  <h3 class="text-xl font-semibold mt-6 mb-3">Windows / macOS</h3>
  <p class="text-muted-foreground mb-4">
    Use Docker Desktop with WSL2 backend (Windows) or the native Docker Desktop
    (macOS). Both Intel and Apple Silicon Macs are supported.
  </p>
</div>
