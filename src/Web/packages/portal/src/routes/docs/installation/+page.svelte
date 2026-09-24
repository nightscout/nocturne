<script lang="ts">
  import { resolve } from "$app/paths";
  import { Button } from "@nocturne/ui/ui/button";
  import { ArrowRight, ChevronDown, ExternalLink, MapPin } from "@lucide/svelte";
  import SystemRequirements from "$lib/components/docs/SystemRequirements.svelte";
  import PikaPodsVoteCard from "$lib/components/PikaPodsVoteCard.svelte";

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
      href: resolve("/docs/installation/docker-compose"),
      logo: "/logos/docker-compose.png",
      title: "Docker Compose",
      desc: "Deploy directly on any Linux server, VPS, or Raspberry Pi using Docker Compose from the command line.",
    },
    {
      href: resolve("/docs/installation/portainer"),
      logo: "/logos/portainer.jpg",
      title: "Portainer",
      desc: "Deploy using the Portainer web interface. Great for managing your stack visually without SSH access.",
    },
    {
      href: resolve("/docs/installation/oracle-cloud"),
      title: "Oracle Cloud",
      desc: "One command in the browser sets up a free server, HTTPS and Nocturne. No SSH or Docker knowledge needed.",
    },
    {
      href: resolve("/docs/installation/byo-postgres"),
      title: "Bring Your Own PostgreSQL",
      desc: "Use a managed PostgreSQL service (RDS, Cloud SQL, Supabase, Neon) or an existing shared database instance. Requires a one-time role bootstrap.",
    },
    {
      href: resolve("/docs/installation/reverse-proxy"),
      title: "Bring Your Own Reverse Proxy",
      desc: "Terminate TLS with nginx, Traefik, or an existing edge instead of the bundled Caddy. Covers the forwarded headers Nocturne requires.",
    },
  ];

  const managedProviders = shuffle([
    {
      name: "nocturne.run",
      url: "https://nocturne.run",
      location: "Germany",
      license: "Commercial",
      blurb:
        "The official managed Nocturne instance, run by the creator of the project. Automatic updates, daily backups, and zero maintenance. Just connect your CGM and go.",
    },
  ]);
</script>

<div class="max-w-3xl">
  <h1 class="text-4xl font-bold tracking-tight mb-4">Installation Guide</h1>
  <p class="text-lg text-muted-foreground mb-8">
    Choose a deployment method below to get Nocturne running on your
    infrastructure. All methods use the same Docker images and configuration.
  </p>

  <h2 class="text-2xl font-bold mt-8 mb-4">System Requirements</h2>
  <SystemRequirements />

  <h2 class="text-2xl font-bold mt-8 mb-4">Choose Your Platform</h2>
  <ul class="not-prose m-0 p-0 list-none border-t border-border">
    {#each PLATFORMS as platform (platform.title)}
      <li class="border-b border-border">
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- platform.href is resolve()d in PLATFORMS -->
        <a href={platform.href} class="group flex items-start gap-4 py-5 no-underline text-inherit">
          {#if platform.logo}
            <img src={platform.logo} alt="" class="size-8 rounded-md object-contain shrink-0 mt-0.5" />
          {:else}
            <span class="size-8 shrink-0" aria-hidden="true"></span>
          {/if}
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

  <h2 class="text-2xl font-bold mt-12 mb-4">Managed Instances</h2>
  <p class="text-muted-foreground mb-4">
    Don't want to self-host? These Nocturne-as-a-Service providers handle the
    infrastructure so you can focus on your diabetes management.
  </p>
  <ul class="not-prose m-0 mb-4 p-0 list-none border-t border-border">
    {#each managedProviders as provider (provider.url)}
      <li class="border-b border-border">
        <a
          href={provider.url}
          target="_blank"
          rel="external noopener noreferrer"
          class="group flex items-start gap-4 py-5 no-underline text-inherit"
        >
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
        We welcome Nocturne-as-a-Service providers. To request a listing, please
        provide the following:
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

  <h2 class="text-2xl font-bold mt-12 mb-4">Platform Notes</h2>

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
