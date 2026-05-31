<script lang="ts">
  import {
    ArrowRight,
    Terminal,
    LayoutDashboard,
    Cloud,
    Database,
    Globe,
    ChevronDown,
    ExternalLink,
    MapPin,
  } from "@lucide/svelte";
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
  <div class="grid gap-4 not-prose">
    <a
      href="/docs/installation/docker-compose"
      class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
    >
      <div class="flex items-start gap-4">
        <div
          class="w-12 h-12 rounded-lg bg-blue-500/15 flex items-center justify-center shrink-0"
        >
          <Terminal class="w-6 h-6 text-blue-500" />
        </div>
        <div class="flex-1">
          <h3
            class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
          >
            Docker Compose
          </h3>
          <p class="text-sm text-muted-foreground">
            Deploy directly on any Linux server, VPS, or Raspberry Pi using
            Docker Compose from the command line.
          </p>
        </div>
        <ArrowRight
          class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors mt-1"
        />
      </div>
    </a>

    <a
      href="/docs/installation/portainer"
      class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
    >
      <div class="flex items-start gap-4">
        <div
          class="w-12 h-12 rounded-lg bg-cyan-500/15 flex items-center justify-center shrink-0"
        >
          <LayoutDashboard class="w-6 h-6 text-cyan-500" />
        </div>
        <div class="flex-1">
          <h3
            class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
          >
            Portainer
          </h3>
          <p class="text-sm text-muted-foreground">
            Deploy using the Portainer web interface. Great for managing your
            stack visually without SSH access.
          </p>
        </div>
        <ArrowRight
          class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors mt-1"
        />
      </div>
    </a>

    <a
      href="/docs/installation/byo-postgres"
      class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
    >
      <div class="flex items-start gap-4">
        <div
          class="w-12 h-12 rounded-lg bg-emerald-500/15 flex items-center justify-center shrink-0"
        >
          <Database class="w-6 h-6 text-emerald-500" />
        </div>
        <div class="flex-1">
          <h3
            class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
          >
            Bring Your Own PostgreSQL
          </h3>
          <p class="text-sm text-muted-foreground">
            Use a managed PostgreSQL service (RDS, Cloud SQL, Supabase, Neon) or
            an existing shared database instance. Requires a one-time role
            bootstrap.
          </p>
        </div>
        <ArrowRight
          class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors mt-1"
        />
      </div>
    </a>

    <div class="p-6 rounded-xl border border-border/60 bg-card/30 opacity-60">
      <div class="flex items-start gap-4">
        <div
          class="w-12 h-12 rounded-lg bg-purple-500/15 flex items-center justify-center shrink-0"
        >
          <Cloud class="w-6 h-6 text-purple-500" />
        </div>
        <div class="flex-1">
          <h3 class="text-lg font-semibold mb-1">Cloud Providers</h3>
          <p class="text-sm text-muted-foreground">
            Guides for GCP, Azure, Heroku, and other cloud platforms are coming
            soon.
          </p>
        </div>
      </div>
    </div>
  </div>

  <h2 class="text-2xl font-bold mt-8 mb-4">Managed Instances</h2>
  <p class="text-muted-foreground mb-4">
    Don't want to self-host? These Nocturne-as-a-Service providers handle the
    infrastructure so you can focus on your diabetes management.
  </p>
  <div class="grid gap-4 not-prose mb-4">
    {#each managedProviders as provider}
      <a
        href={provider.url}
        target="_blank"
        rel="noopener noreferrer"
        class="p-6 rounded-xl border border-border/60 bg-card/50 hover:bg-card hover:border-primary/30 transition-colors group"
      >
        <div class="flex items-start gap-4">
          <div
            class="w-12 h-12 rounded-lg bg-amber-500/15 flex items-center justify-center shrink-0"
          >
            <Globe class="w-6 h-6 text-amber-500" />
          </div>
          <div class="flex-1">
            <h3
              class="text-lg font-semibold mb-1 group-hover:text-primary transition-colors"
            >
              {provider.name}
            </h3>
            <p class="text-sm text-muted-foreground mb-2">
              {provider.blurb}
            </p>
            <div class="flex items-center gap-4 text-xs text-muted-foreground">
              <span class="flex items-center gap-1">
                <MapPin class="w-3 h-3" />
                {provider.location}
              </span>
              <span>{provider.license} license</span>
            </div>
          </div>
          <ExternalLink
            class="w-5 h-5 text-muted-foreground group-hover:text-primary transition-colors mt-1"
          />
        </div>
      </a>
    {/each}
        <PikaPodsVoteCard />
  </div>

  <button
    onclick={() => (showListingRequirements = !showListingRequirements)}
    class="flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground transition-colors cursor-pointer"
  >
    <ChevronDown
      class="w-4 h-4 transition-transform {showListingRequirements
        ? 'rotate-180'
        : ''}"
    />
    Want to list your service here?
  </button>
  {#if showListingRequirements}
    <div
      class="mt-3 p-4 rounded-lg border border-border/60 bg-card/30 text-sm text-muted-foreground"
    >
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
          href="https://github.com/nicknightscout/nocturne"
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
