<script lang="ts">
  import {
    Globe,
    MessageCircle,
    Heart,
    BookOpen,
    Users,
    Megaphone,
    Database,
    ArrowRight,
    ArrowUpRight,
    ExternalLink,
    MessageSquare,
    Tag,
    HeartHandshake,
  } from "@lucide/svelte";
  import { onMount } from "svelte";
  import { Button } from "@nocturne/ui/ui/button";
  import { resolve } from "$app/paths";
  import { LINKS } from "$lib/data/links";
  import { track } from "$lib/analytics";
  import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";

  const BRAND = { background: "var(--brand)", foreground: "white" };

  const STATS = [
    { value: "100%", label: "Built by volunteers" },
    { value: "22+", label: "Devices & apps connected" },
    { value: "0", label: "Ads, cookies, or paywalls" },
    { value: "24/7", label: "Community support" },
  ];

  type Lane = {
    /** Also the `lane` property of a `Get Involved Lane` event; see ALLOWED_PROPS. */
    id: string;
    icon: typeof Globe;
    /** A --contribute-* token from app.css, as var(); the card sets it as --highlight. */
    accent: string;
    title: string;
    desc: string;
    cta: string;
    href: string;
    external?: boolean;
    highlight?: boolean;
  };

  const LANES: Lane[] = [
    {
      id: "translate",
      icon: Globe,
      accent: "var(--contribute-translate)",
      title: "Translate Nocturne",
      desc: "Every interface string lives in a gettext .po file, one per language, and most languages are barely started. Edit one on GitHub and open a pull request: no build tools, just words.",
      cta: "Open the translation files",
      href: LINKS.translationFiles,
      external: true,
    },
    {
      id: "support",
      icon: MessageCircle,
      accent: "var(--contribute-support)",
      title: "Answer questions",
      desc: "New self-hosters get stuck. Hang out in the Discord and help someone get their data flowing. It is the fastest way to make a real difference today.",
      cta: "Join the Discord",
      href: LINKS.discord,
      external: true,
    },
    {
      id: "donate",
      icon: Heart,
      accent: "var(--contribute-donate)",
      title: "Donate",
      desc: "Nocturne is free and always will be. One-off gifts to the Nightscout Foundation and monthly subscriptions from US$10 both cover servers, testing devices, and keep the project independent.",
      cta: "See the ways to give",
      href: "#donate",
      highlight: true,
    },
    {
      id: "docs",
      icon: BookOpen,
      accent: "var(--contribute-docs)",
      title: "Improve the docs",
      desc: "Spotted a gap, a stale screenshot, or a typo? Clear docs save everyone hours. Fix a page or write a guide for the setup you wish you'd had.",
      cta: "Browse the docs",
      href: resolve("/docs"),
    },
    {
      id: "peer",
      icon: Users,
      accent: "var(--contribute-peer)",
      title: "Peer support",
      desc: 'Plenty of people start in the "CGM in the Cloud" Facebook group and community forums. Share what you\'ve learned where newcomers actually ask.',
      cta: "Open CGM in the Cloud",
      href: LINKS.facebook,
      external: true,
    },
    {
      id: "spread",
      icon: Megaphone,
      accent: "var(--contribute-spread)",
      title: "Spread the word",
      desc: "Write up your setup, post your time-in-range win, give a talk at your clinic. Word of mouth is how most people find Nightscout in the first place. Send us your story and we'll help share it.",
      cta: "Email testimonials@nocturne.run",
      href: LINKS.testimonials,
      external: true,
    },
    {
      id: "sponsor",
      icon: HeartHandshake,
      accent: "var(--contribute-sponsor)",
      title: "Sponsor Hack Diabetes",
      desc: "Hack Diabetes brings the open-source diabetes community together to build and test tools like Nocturne. Sponsors fund the events and get their name in front of the people who build this software.",
      cta: "Sponsor an event",
      href: LINKS.hackDiabetes,
      external: true,
    },
    {
      id: "data",
      icon: Database,
      accent: "var(--contribute-data)",
      title: "Donate anonymized data",
      desc: "Opt in to share de-identified glucose data so connectors and reports can be tested against real-world patterns, not just synthetic samples.",
      cta: "Email research-data@nocturne.run",
      href: LINKS.researchData,
      external: true,
    },
  ];

  const LABEL_ACCENTS: Record<string, string> = {
    "get-involved": "var(--brand)",
    i18n: "var(--contribute-translate)",
    documentation: "var(--contribute-docs)",
    testing: "var(--contribute-spread)",
    triage: "var(--contribute-support)",
    tutorial: "var(--contribute-peer)",
    "good first issue": "var(--contribute-donate)",
  };

  type Issue = {
    num: number;
    title: string;
    url: string;
    labels: string[];
    comments: number;
    updatedAt: string;
  };

  type GhLabel = { name: string };
  type GhIssue = {
    number: number;
    title: string;
    html_url: string;
    labels: (GhLabel | string)[];
    comments: number;
    updated_at: string;
    pull_request?: unknown;
  };

  type FeedStatus = "loading" | "ready" | "error";

  // GitHub's REST API is public (CORS-enabled, ~60 req/hr per visitor IP),
  // so the feed is fetched live in the browser rather than baked in at build
  // time, keeping these tasks genuinely grabbable and up to date.
  const ISSUES_API =
    "https://api.github.com/repos/nightscout/nocturne/issues?labels=get-involved&state=open&sort=updated&direction=desc&per_page=8";

  let issues = $state.raw<Issue[]>([]);
  let status = $state<FeedStatus>("loading");

  onMount(async () => {
    try {
      const res = await fetch(ISSUES_API, {
        headers: { Accept: "application/vnd.github+json" },
      });
      if (!res.ok) throw new Error(`GitHub API responded ${res.status}`);
      const data: GhIssue[] = await res.json();
      issues = data
        .filter((item) => !item.pull_request)
        .map((item) => ({
          num: item.number,
          title: item.title,
          url: item.html_url,
          labels: (item.labels ?? []).map((l) =>
            typeof l === "string" ? l : l.name,
          ),
          comments: item.comments ?? 0,
          updatedAt: item.updated_at,
        }));
      status = "ready";
    } catch {
      status = "error";
    }
  });

  function relativeTime(iso: string): string {
    const mins = (Date.now() - new Date(iso).getTime()) / 60000;
    const hours = mins / 60;
    const days = hours / 24;
    const weeks = days / 7;
    if (mins < 1) return "just now";
    if (mins < 60) return `${Math.floor(mins)}m`;
    if (hours < 24) return `${Math.floor(hours)}h`;
    if (days < 7) return `${Math.floor(days)}d`;
    if (weeks < 5) return `${Math.floor(weeks)}w`;
    return `${Math.floor(days / 30)}mo`;
  }

  // mailto: hands off to the mail client, so a new tab would just be left orphaned.
  function opensNewTab(lane: Lane): boolean {
    return Boolean(lane.external) && !lane.href.startsWith("mailto:");
  }
</script>

<svelte:head>
  <title>Get Involved - Nocturne</title>
  <meta
    name="description"
    content="Nocturne is built by volunteers. You don't need to write code to contribute. Here's where to start."
  />
</svelte:head>

<div class="container mx-auto px-4 sm:px-7">
  <!-- Hero -->
  <section class="pt-14 pb-10">
    <div>
      <span
        class="inline-flex items-center gap-2 whitespace-nowrap text-xs font-medium tracking-widest uppercase text-muted-foreground bg-card/50 border border-border px-3 py-1.5 rounded-full backdrop-blur-sm"
      >
        <span class="w-1.5 h-1.5 rounded-full inline-block bg-brand"></span>
        Get Involved
      </span>
      <h1
        class="text-4xl font-bold tracking-tight leading-tight mt-4.5 mb-3.5"
      >
        The best diabetes tools are built by <span class="gi-accent-text"
          >the people who need them</span
        >.
      </h1>
      <p class="text-muted-foreground text-lead max-w-[520px] mb-6.5">
        Nocturne is free, open source, and made entirely by volunteers. You
        don't need to write a line of code to move it forward. Here's where to
        start.
      </p>
      <div class="flex gap-2.5 flex-wrap">
        <Button href="#tasks" variant="brand" size="cta" brand={BRAND}>
          Find a task <ArrowRight strokeWidth={2.5} />
        </Button>
        <Button
          href={LINKS.discord}
          target="_blank"
          rel="external noopener noreferrer"
          onclick={() => track("Outbound Click", { destination: "discord" })}
          variant="outline"
          size="cta"
        >
          <MessageCircle /> Join the Discord
        </Button>
      </div>
    </div>

    <!-- Stat widgets -->
    <div class="grid grid-cols-2 sm:grid-cols-4 gap-3 mt-10">
      {#each STATS as stat, si (si)}
        <div class="bg-card border border-border rounded-xl p-5">
          <p class="text-xs font-semibold tracking-widest uppercase text-muted-foreground m-0 mb-2.5">
            {stat.label}
          </p>
          <div class="text-4xl font-bold tabular-nums tracking-tight leading-none text-foreground">
            {stat.value}
          </div>
        </div>
      {/each}
    </div>
  </section>

  <!-- Ways to help -->
  <section class="py-8" id="ways">
    <div class="mb-6">
      <p class="text-xs font-semibold tracking-widest uppercase m-0 mb-3.5 text-brand">
        Ways to help
      </p>
      <h2 class="text-3xl font-bold tracking-tight">
        Ways to contribute
      </h2>
    </div>
    <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
      {#each LANES as lane (lane.id)}
        <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- lane.href is a resolve()d route, a fragment, or an external URL from LANES -->
        <a href={lane.href}
          target={opensNewTab(lane) ? "_blank" : undefined}
          rel={opensNewTab(lane) ? "noopener noreferrer" : undefined}
          onclick={() => track("Get Involved Lane", { lane: lane.id })}
          class="gi-lane-card flex flex-row items-start gap-4 bg-card border border-border rounded-xl p-5 transition duration-200 no-underline text-inherit"
          class:gi-lane-highlight={lane.highlight}
          style:--highlight={lane.accent}
        >
          <div class="w-[42px] h-[42px] rounded-lg grid place-items-center shrink-0 bg-highlight/15">
            <lane.icon class="w-[22px] h-[22px] text-highlight" />
          </div>
          <div class="flex-1 flex flex-col">
            <h3 class="text-lg font-semibold mb-1.5 tracking-tight">
              {lane.title}
            </h3>
            <p class="text-sm text-muted-foreground leading-relaxed flex-1">
              {lane.desc}
            </p>
            <span class="gi-lane-link inline-flex items-center gap-1.5 mt-3 text-sm font-semibold text-highlight">
              {lane.cta}
              {#if lane.external}
                <ArrowUpRight class="w-[15px] h-[15px]" />
              {:else}
                <ArrowRight class="w-[15px] h-[15px]" />
              {/if}
            </span>
          </div>
        </a>
      {/each}
    </div>
  </section>

  <!-- Open tasks -->
  <section class="py-8" id="tasks">
    <div class="mb-5 flex items-end justify-between gap-4 flex-wrap">
      <div>
        <p class="text-xs font-semibold tracking-widest uppercase m-0 mb-3.5 text-brand">
          Open tasks right now
        </p>
        <h2 class="text-3xl font-bold tracking-tight m-0">
          Tagged <code class="text-xl">get-involved</code>
        </h2>
      </div>
      <a
        href={LINKS.githubLabel}
        target="_blank"
        rel="external noopener noreferrer"
        onclick={() => track("Outbound Click", { destination: "github-labels" })}
        class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-sm h-[38px] px-4 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 bg-transparent border border-border text-foreground hover:bg-accent"
      >
        Open the tracker <ExternalLink class="w-3.5 h-3.5" />
      </a>
    </div>

    <!-- Issue feed card -->
    <div class="border border-border rounded-xl bg-card overflow-hidden">
      <div class="flex items-center gap-3 px-5.5 py-4.5 border-b border-border bg-background/35">
        <img
          src="/logos/github.png"
          alt="GitHub"
          class="w-[22px] h-[22px] rounded-sm object-contain"
          onerror={(e) => { if (e.currentTarget instanceof HTMLElement) e.currentTarget.style.display = 'none'; }}
        />
        <span class="font-mono text-sm text-muted-foreground">nightscout/nocturne</span>
        <span
          class="inline-flex items-center gap-1.5 whitespace-nowrap text-xs font-semibold py-1 px-2.5 rounded-full ml-auto bg-brand/15 text-brand border border-brand/30"
        >
          <Tag class="w-[13px] h-[13px]" />
          get-involved
        </span>
      </div>

      {#if status === "loading"}
        {#each Array.from({ length: 5 }) as _, i (i)}
          <div class="flex items-center gap-3.5 px-4.5 py-3.25 border-b border-border last:border-b-0">
            <div class="shrink-0 w-3.5 h-3.5 rounded-full bg-muted animate-pulse"></div>
            <div class="flex-1 min-w-0 space-y-2">
              <div class="h-3.5 w-2/3 rounded bg-muted animate-pulse"></div>
              <div class="h-2.5 w-28 rounded bg-muted animate-pulse"></div>
            </div>
          </div>
        {/each}
      {:else if status === "error"}
        <div class="px-5.5 py-10 text-center text-sm text-muted-foreground">
          Couldn't load live tasks just now.
          <a
            href={LINKS.githubLabel}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Outbound Click", { destination: "github-labels" })}
            class="font-semibold underline text-brand">View them on GitHub</a
          >.
        </div>
      {:else if issues.length === 0}
        <div class="px-5.5 py-10 text-center text-sm text-muted-foreground">
          No open tasks tagged <code>get-involved</code> right now. Check back soon,
          or
          <a
            href={LINKS.discord}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Outbound Click", { destination: "discord" })}
            class="font-semibold underline text-brand">ask in the Discord</a
          >.
        </div>
      {:else}
        {#each issues as issue (issue.num)}
          <a
            href={issue.url}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Outbound Click", { destination: "github" })}
            class="flex items-center gap-3.5 px-4.5 py-3.25 border-b border-border transition-colors duration-150 cursor-pointer no-underline text-inherit hover:bg-card/80 last:border-b-0"
          >
            <div class="gi-issue-dot shrink-0 w-3.5 h-3.5 rounded-full relative mt-0.75 border-2 border-brand">
            </div>
            <div class="flex-1 min-w-0">
              <p class="text-sm font-semibold m-0 flex items-baseline gap-2 flex-wrap">
                {issue.title}
                <span class="font-mono text-xs font-medium text-muted-foreground">#{issue.num}</span>
              </p>
              <div class="flex items-center gap-2 flex-wrap mt-1.5">
                {#each issue.labels as label (label)}
                  <span
                    class="text-2xs font-semibold whitespace-nowrap py-0.75 px-2.25 rounded-full text-highlight bg-highlight/16 border border-highlight/30"
                    style:--highlight={LABEL_ACCENTS[label] ?? "var(--muted-foreground)"}
                  >
                    {label}
                  </span>
                {/each}
              </div>
            </div>
            <div class="flex items-center gap-4 shrink-0 text-muted-foreground text-xs">
              <span class="inline-flex items-center gap-1.5">
                <MessageSquare class="w-3.5 h-3.5" /> {issue.comments}
              </span>
              <span>{relativeTime(issue.updatedAt)}</span>
            </div>
          </a>
        {/each}
      {/if}

      <div class="flex items-center justify-between gap-3 px-5.5 py-4">
        <span class="text-muted-foreground text-xs">Updated continuously: these are real, grabbable tasks.</span>
        <a
          href={LINKS.githubLabel}
          target="_blank"
          rel="external noopener noreferrer"
          onclick={() => track("Outbound Click", { destination: "github-labels" })}
          class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-sm h-[38px] px-4 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 bg-transparent border border-border text-foreground hover:bg-accent"
        >
          View all on GitHub <ExternalLink class="w-3.5 h-3.5" />
        </a>
      </div>
    </div>
  </section>

  <!-- Donate band -->
  <section class="pb-20" id="donate">
    <div class="gi-donate rounded-2xl p-10 flex items-center gap-8 flex-wrap border border-brand/45">
      <div class="flex-1 min-w-[280px]">
        <h3 class="text-2xl font-bold tracking-tight mb-2">
          Keep Nocturne free and independent
        </h3>
        <p class="text-muted-foreground m-0 max-w-[52ch]">
          There is no company behind Nocturne, just volunteers and the
          Nightscout Foundation, a registered non-profit. Donations cover
          servers, test devices, and the work that keeps your data yours. Give
          once, or subscribe monthly.
        </p>
      </div>
      <div class="flex flex-col gap-2.5">
        <Button
          href={LINKS.donate}
          target="_blank"
          rel="external noopener noreferrer"
          onclick={() => track("Donate Click", { destination: "foundation" })}
          variant="brand"
          size="cta"
          brand={BRAND}
        >
          <HeartHandshake /> Donate to the Foundation
        </Button>
        <span class="text-xs text-muted-foreground text-center"
          >Tax-deductible in the US &middot; Supports the whole community</span
        >
      </div>
    </div>

    <SupportNocturne />
  </section>
</div>

<style>
  .gi-accent-text {
    background: linear-gradient(118deg, var(--foreground), color-mix(in oklch, var(--brand), var(--foreground) 35%));
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
  }

  .gi-lane-card:hover {
    border-color: color-mix(in oklch, var(--brand), transparent 55%);
  }

  .gi-lane-highlight {
    border-color: color-mix(in oklch, var(--brand), transparent 45%);
    background: color-mix(in oklch, var(--brand), var(--card) 80%);
  }

  .gi-donate {
    background:
      radial-gradient(120% 140% at 100% 0%, color-mix(in oklch, var(--brand), transparent 80%), transparent 60%),
      color-mix(in oklch, var(--card), transparent 30%);
  }

  .gi-lane-link :global(svg) {
    transition: transform 0.15s;
  }

  .gi-lane-card:hover .gi-lane-link :global(svg) {
    transform: translateX(3px);
  }

  .gi-issue-dot::after {
    content: "";
    position: absolute;
    inset: 3px;
    border-radius: 50%;
    background: var(--brand);
  }

</style>
