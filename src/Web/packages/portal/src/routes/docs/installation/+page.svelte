<script lang="ts">
  import { resolve } from "$app/paths";
  import { ArtworkHero } from "water-ui";
  import { DOCS_HERO } from "#lib/data/docs-artwork.js";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import SystemRequirements from "#lib/components/docs/SystemRequirements.svelte";
  import { track } from "#lib/analytics.js";
  import HostingPicker from "#lib/components/docs/hosting/HostingPicker.svelte";

  const PLATFORMS = [
    {
      id: "install-script",
      href: resolve("/docs/installation/install-script"),
      logo: "/logos/nocturne.png",
      title: "Install script",
      desc: "Recommended. One script installs Docker and Nocturne on a Linux server, VPS, or Raspberry Pi. You do not need to know Docker.",
    },
    {
      id: "docker-compose",
      href: resolve("/docs/installation/docker-compose"),
      logo: "/logos/docker-compose.png",
      title: "Docker Compose",
      desc: "Do the same steps by hand with Docker Compose. Use this guide for your own reverse proxy or a host directory for the database.",
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
      desc: "No server yet? One command in the browser makes a free one and runs the install script on it. No SSH or Docker knowledge needed.",
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
      href: resolve("docs/installation/reverse-proxy"),
      logo: "/logos/nginx.svg",
      title: "Bring your own reverse proxy",
      desc: "Terminate TLS with nginx, Traefik, or an existing edge instead of the bundled Caddy. Covers the forwarded headers Nocturne requires.",
    },
  ];

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
    These guides are for when you run Nocturne on your own server. If you have a
    Linux server, use the install script. The other guides are for
    a server that you do not have yet, or for a setup that you manage yourself. All
    methods use the same Docker images and configuration.
  </p>

  <HostingPicker current="self-host" />

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
