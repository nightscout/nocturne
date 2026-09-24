<script lang="ts">
  import {
    MessageCircle,
    ArrowRight,
    ArrowUpRight,
    ExternalLink,
    MessageSquare,
    HeartHandshake,
  } from "@lucide/svelte";
  import { onMount } from "svelte";
  import { Button } from "@nocturne/ui/ui/button";
  import { resolve } from "$app/paths";
  import { LINKS } from "$lib/data/links";
  import { track } from "$lib/analytics";
  import SupportNocturne from "$lib/components/docs/SupportNocturne.svelte";

  const BRAND = { background: "var(--brand)", foreground: "white" };

  type Lane = {
    /** Also the `lane` property of a `Get Involved Lane` event; see ALLOWED_PROPS. */
    id: string;
    title: string;
    desc: string;
    cta: string;
    href: string;
    external?: boolean;
  };

  const LANES: Lane[] = [
    {
      id: "translate",
      title: "Translate Nocturne",
      desc: "Every interface string lives in a gettext .po file, one per language, and most languages are barely started. Edit one on GitHub and open a pull request: no build tools, just words.",
      cta: "Open the translation files",
      href: LINKS.translationFiles,
      external: true,
    },
    {
      id: "support",
      title: "Answer questions",
      desc: "New self-hosters get stuck. Hang out in the Discord and help someone get their data flowing. It is the fastest way to make a real difference today.",
      cta: "Join the Discord",
      href: LINKS.discord,
      external: true,
    },
    {
      id: "donate",
      title: "Donate",
      desc: "Nocturne is free and always will be. One-off gifts to the Nightscout Foundation and monthly subscriptions from US$10 both cover servers, testing devices, and keep the project independent.",
      cta: "See the ways to give",
      href: "#donate",
    },
    {
      id: "docs",
      title: "Improve the docs",
      desc: "Spotted a gap, a stale screenshot, or a typo? Clear docs save everyone hours. Fix a page or write a guide for the setup you wish you'd had.",
      cta: "Browse the docs",
      href: resolve("/docs"),
    },
    {
      id: "peer",
      title: "Peer support",
      desc: 'Plenty of people start in the "CGM in the Cloud" Facebook group and community forums. Share what you\'ve learned where newcomers actually ask.',
      cta: "Open CGM in the Cloud",
      href: LINKS.facebook,
      external: true,
    },
    {
      id: "spread",
      title: "Spread the word",
      desc: "Write up your setup, post your time-in-range win, give a talk at your clinic. Word of mouth is how most people find Nightscout in the first place. Send us your story and we'll help share it.",
      cta: "Email testimonials@nocturne.run",
      href: LINKS.testimonials,
      external: true,
    },
    {
      id: "sponsor",
      title: "Sponsor Hack Diabetes",
      desc: "Hack Diabetes brings the open-source diabetes community together to build and test tools like Nocturne. Sponsors fund the events and get their name in front of the people who build this software.",
      cta: "Sponsor an event",
      href: LINKS.hackDiabetes,
      external: true,
    },
    {
      id: "data",
      title: "Donate anonymized data",
      desc: "Opt in to share de-identified glucose data so connectors and reports can be tested against real-world patterns, not just synthetic samples.",
      cta: "Email research-data@nocturne.run",
      href: LINKS.researchData,
      external: true,
    },
  ];

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
          labels: (item.labels ?? [])
            .map((l) => (typeof l === "string" ? l : l.name))
            .filter((l) => l !== "get-involved"),
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

<div class="max-w-[1200px] mx-auto px-6">
  <section class="pt-20 pb-16 max-w-[760px]">
    <h1 class="text-headline font-bold text-foreground m-0 mb-5 text-balance">
      The best diabetes tools are built by the people who need them.
    </h1>
    <p class="text-lead text-muted-foreground m-0 mb-8 max-w-[560px]">
      Nocturne is free, open source, and made entirely by volunteers. You
      don't need to write a line of code to move it forward. Here's where to
      start.
    </p>
    <div class="flex gap-3 flex-wrap">
      <Button href="#tasks" variant="brand" size="cta" brand={BRAND}>
        Find a task <ArrowRight />
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
  </section>

  <section class="py-16 border-t border-border" id="ways">
    <h2 class="text-section font-bold text-foreground m-0 mb-10">Ways to contribute</h2>
    <ul class="m-0 p-0 list-none grid gap-x-16 md:grid-cols-2 border-t border-border md:border-t-0">
      {#each LANES as lane, i (lane.id)}
        <li class="py-7 border-b border-border {i < 2 ? 'md:border-t' : ''}">
          <h3 class="text-lg font-semibold text-foreground m-0 mb-2">{lane.title}</h3>
          <p class="text-muted-foreground leading-relaxed m-0 mb-3 max-w-[60ch]">{lane.desc}</p>
          <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- lane.href is a resolve()d route, a fragment, or an external URL from LANES -->
          <a href={lane.href}
            target={opensNewTab(lane) ? "_blank" : undefined}
            rel={opensNewTab(lane) ? "noopener noreferrer" : undefined}
            onclick={() => track("Get Involved Lane", { lane: lane.id })}
            class="group inline-flex items-center gap-1.5 text-sm font-semibold text-brand no-underline hover:underline underline-offset-4"
          >
            {lane.cta}
            {#if lane.external}
              <ArrowUpRight class="size-4" aria-hidden="true" />
            {:else}
              <ArrowRight class="size-4 transition-transform group-hover:translate-x-0.5 motion-reduce:transition-none" aria-hidden="true" />
            {/if}
          </a>
        </li>
      {/each}
    </ul>
  </section>

  <section class="py-16 border-t border-border" id="tasks">
    <div class="mb-8 flex items-end justify-between gap-4 flex-wrap">
      <div>
        <h2 class="text-section font-bold text-foreground m-0 mb-3">Open tasks</h2>
        <p class="text-muted-foreground m-0">
          Issues tagged <code class="text-sm">get-involved</code> on GitHub, live.
        </p>
      </div>
      <Button
        href={LINKS.githubLabel}
        target="_blank"
        rel="external noopener noreferrer"
        onclick={() => track("Outbound Click", { destination: "github-labels" })}
        variant="outline"
      >
        View all on GitHub <ExternalLink />
      </Button>
    </div>

    <div class="border-t border-border">
      {#if status === "loading"}
        {#each Array.from({ length: 5 }) as _, i (i)}
          <div class="py-4 border-b border-border space-y-2">
            <div class="h-3.5 w-2/3 rounded bg-muted animate-pulse"></div>
            <div class="h-2.5 w-28 rounded bg-muted animate-pulse"></div>
          </div>
        {/each}
      {:else if status === "error"}
        <p class="py-10 m-0 text-sm text-muted-foreground">
          Couldn't load live tasks just now.
          <a
            href={LINKS.githubLabel}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Outbound Click", { destination: "github-labels" })}
            class="font-semibold underline text-brand">View them on GitHub</a
          >.
        </p>
      {:else if issues.length === 0}
        <p class="py-10 m-0 text-sm text-muted-foreground">
          No open tasks tagged <code>get-involved</code> right now. Check back soon,
          or
          <a
            href={LINKS.discord}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Outbound Click", { destination: "discord" })}
            class="font-semibold underline text-brand">ask in the Discord</a
          >.
        </p>
      {:else}
        {#each issues as issue (issue.num)}
          <a
            href={issue.url}
            target="_blank"
            rel="external noopener noreferrer"
            onclick={() => track("Outbound Click", { destination: "github" })}
            class="flex items-start gap-4 py-4 border-b border-border no-underline text-inherit hover:bg-accent/40 transition-colors -mx-3 px-3 rounded-md"
          >
            <div class="flex-1 min-w-0">
              <p class="font-medium text-foreground m-0">
                {issue.title}
                <span class="text-sm font-normal text-muted-foreground tabular-nums">#{issue.num}</span>
              </p>
              {#if issue.labels.length > 0}
                <p class="text-xs text-muted-foreground m-0 mt-1">{issue.labels.join(" · ")}</p>
              {/if}
            </div>
            <div class="flex items-center gap-4 shrink-0 text-muted-foreground text-xs tabular-nums pt-1">
              <span class="inline-flex items-center gap-1.5">
                <MessageSquare class="size-3.5" aria-hidden="true" />
                <span class="sr-only">Comments:</span>
                {issue.comments}
              </span>
              <span>{relativeTime(issue.updatedAt)}</span>
            </div>
          </a>
        {/each}
      {/if}
    </div>
  </section>

  <section class="py-16 border-t border-border" id="donate">
    <div class="grid gap-8 md:grid-cols-[minmax(0,7fr)_minmax(0,5fr)] md:items-end">
      <div>
        <h2 class="text-section font-bold text-foreground m-0 mb-4">Keep Nocturne free and independent</h2>
        <p class="text-muted-foreground text-lead m-0 max-w-[56ch]">
          There is no company behind Nocturne, just volunteers and the
          Nightscout Foundation, a registered non-profit. Donations cover
          servers, test devices, and the work that keeps your data yours. Give
          once, or subscribe monthly.
        </p>
      </div>
      <div class="flex flex-col gap-2.5 md:items-end">
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
        <span class="text-xs text-muted-foreground">Tax-deductible in the US &middot; Supports the whole community</span>
      </div>
    </div>

    <SupportNocturne />
  </section>
</div>
