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

  const ACCENT = "oklch(0.6 0.118 184.704)";

  const LINKS = {
    discord: "https://discord.gg/sKEhtHeb2z",
    donate: "https://www.nightscoutfoundation.org/donate",
    githubLabel: "https://github.com/nightscout/nocturne/labels/get-involved",
    github: "https://github.com/nightscout/nocturne",
  };

  const STATS = [
    { value: "100%", label: "Built by volunteers" },
    { value: "22+", label: "Devices & apps connected" },
    { value: "0", label: "Ads, trackers, or paywalls" },
    { value: "24/7", label: "Community support" },
  ];

  type Lane = {
    id: string;
    icon: typeof Globe;
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
      accent: "oklch(0.65 0.16 250)",
      title: "Translate Nocturne",
      desc: "Help people read Nocturne in their own language. Pick a locale, translate the interface strings — no code, no build tools, just words.",
      cta: "Start translating",
      href: "#tasks",
    },
    {
      id: "support",
      icon: MessageCircle,
      accent: "oklch(0.62 0.17 280)",
      title: "Answer questions",
      desc: "New self-hosters get stuck. Hang out in the Discord and help someone get their data flowing — the fastest way to make a real difference today.",
      cta: "Join the Discord",
      href: LINKS.discord,
      external: true,
    },
    {
      id: "donate",
      icon: Heart,
      accent: "oklch(0.62 0.2 18)",
      title: "Donate",
      desc: "Nocturne is free and always will be. Donations to the Nightscout Foundation cover servers, testing devices, and keep the project independent.",
      cta: "Donate via the Foundation",
      href: LINKS.donate,
      external: true,
      highlight: true,
    },
    {
      id: "docs",
      icon: BookOpen,
      accent: "oklch(0.65 0.15 160)",
      title: "Improve the docs",
      desc: "Spotted a gap, a stale screenshot, or a typo? Clear docs save everyone hours. Fix a page or write a guide for the setup you wish you'd had.",
      cta: "Browse the docs",
      href: "#tasks",
    },
    {
      id: "peer",
      icon: Users,
      accent: "oklch(0.66 0.15 70)",
      title: "Peer support",
      desc: 'Plenty of people start in the "CGM in the Cloud" Facebook group and community forums. Share what you\'ve learned where newcomers actually ask.',
      cta: "Visit the forums",
      href: "#tasks",
    },
    {
      id: "spread",
      icon: Megaphone,
      accent: "oklch(0.68 0.16 40)",
      title: "Spread the word",
      desc: "Write up your setup, post your time-in-range win, give a talk at your clinic. Word of mouth is how most people find Nightscout in the first place.",
      cta: "Share your story",
      href: "#tasks",
    },
    {
      id: "data",
      icon: Database,
      accent: "oklch(0.6 0.13 200)",
      title: "Donate anonymized data",
      desc: "Opt in to share de-identified glucose data so connectors and reports can be tested against real-world patterns — not just synthetic samples.",
      cta: "Learn how it works",
      href: "#tasks",
    },
  ];

  const LABEL_COLORS: Record<string, { fg: string; bg: string }> = {
    "get-involved": {
      fg: "oklch(0.62 0.118 184.7)",
      bg: "oklch(0.62 0.118 184.7 / 0.16)",
    },
    translation: {
      fg: "oklch(0.65 0.16 250)",
      bg: "oklch(0.65 0.16 250 / 0.16)",
    },
    documentation: {
      fg: "oklch(0.65 0.15 160)",
      bg: "oklch(0.65 0.15 160 / 0.16)",
    },
    testing: {
      fg: "oklch(0.68 0.16 40)",
      bg: "oklch(0.68 0.16 40 / 0.16)",
    },
    triage: {
      fg: "oklch(0.62 0.17 280)",
      bg: "oklch(0.62 0.17 280 / 0.16)",
    },
    tutorial: {
      fg: "oklch(0.66 0.15 70)",
      bg: "oklch(0.66 0.15 70 / 0.16)",
    },
    "good first issue": {
      fg: "oklch(0.62 0.2 18)",
      bg: "oklch(0.62 0.2 18 / 0.16)",
    },
  };

  type Issue = {
    num: number;
    title: string;
    labels: string[];
    comments: number;
    updated: string;
  };

  const ISSUES: Issue[] = [
    {
      num: 482,
      title: "Translate the onboarding flow into Spanish",
      labels: ["get-involved", "translation"],
      comments: 4,
      updated: "2h",
    },
    {
      num: 471,
      title: "Document the Glooko connector setup end-to-end",
      labels: ["get-involved", "documentation", "good first issue"],
      comments: 2,
      updated: "6h",
    },
    {
      num: 465,
      title: "Verify Nocturne with Dexcom G7 (EU firmware)",
      labels: ["get-involved", "testing"],
      comments: 9,
      updated: "1d",
    },
    {
      num: 460,
      title: 'Write a "your first 24 hours" guide for new self-hosters',
      labels: ["get-involved", "documentation"],
      comments: 1,
      updated: "2d",
    },
    {
      num: 455,
      title: "Record a screencast: setting up glucose alarms",
      labels: ["get-involved", "tutorial"],
      comments: 3,
      updated: "3d",
    },
    {
      num: 449,
      title: "Triage stale issues in the connectors backlog",
      labels: ["get-involved", "triage"],
      comments: 6,
      updated: "4d",
    },
  ];

  function resolveHref(href: string): string {
    return (LINKS as Record<string, string>)[href] || href;
  }
</script>

<svelte:head>
  <title>Get Involved - Nocturne</title>
  <meta
    name="description"
    content="Nocturne is built by volunteers. You don't need to write code to contribute — here's where to start."
  />
</svelte:head>

<div class="container mx-auto px-4 sm:px-7">
  <!-- Hero -->
  <section class="pt-14 pb-10">
    <div>
      <span
        class="inline-flex items-center gap-2 whitespace-nowrap text-xs font-medium tracking-[0.08em] uppercase text-muted-foreground bg-[color-mix(in_oklch,var(--card),transparent_50%)] border border-border px-3 py-1.5 rounded-full backdrop-blur-sm"
      >
        <span class="w-1.5 h-1.5 rounded-full inline-block bg-[--accent-dot]" style="--accent-dot: {ACCENT}"></span>
        Get Involved
      </span>
      <h1
        class="text-[42px] font-bold tracking-[-0.025em] leading-[1.08] mt-[18px] mb-3.5"
      >
        The best diabetes tools are built by <span class="gi-accent-text"
          >the people who need them</span
        >.
      </h1>
      <p class="text-muted-foreground text-[17px] max-w-[520px] mb-[26px]">
        Nocturne is free, open source, and made entirely by volunteers. You
        don't need to write a line of code to move it forward — here's where to
        start.
      </p>
      <div class="flex gap-2.5 flex-wrap">
        <a
          href="#tasks"
          class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-[15px] h-[46px] px-6 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 bg-[--gi-accent] text-white hover:brightness-108"
          style="--gi-accent: {ACCENT}"
        >
          Find a task <ArrowRight class="w-4 h-4" strokeWidth={2.5} />
        </a>
        <a
          href={LINKS.discord}
          target="_blank"
          rel="noopener noreferrer"
          class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-[15px] h-[46px] px-6 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 bg-transparent border border-border text-foreground hover:bg-accent"
        >
          <MessageCircle class="w-4 h-4" /> Join the Discord
        </a>
      </div>
    </div>

    <!-- Stat widgets -->
    <div class="grid grid-cols-2 sm:grid-cols-4 gap-3 mt-10">
      {#each STATS as stat}
        <div class="bg-card border border-border rounded-xl p-5">
          <p class="text-[11px] font-semibold tracking-[0.08em] uppercase text-muted-foreground m-0 mb-2.5">
            {stat.label}
          </p>
          <div class="text-4xl font-bold tabular-nums tracking-[-0.03em] leading-none text-foreground">
            {stat.value}
          </div>
        </div>
      {/each}
    </div>
  </section>

  <!-- Ways to help -->
  <section class="py-8" id="ways">
    <div class="mb-6">
      <p
        class="text-xs font-semibold tracking-[0.1em] uppercase m-0 mb-3.5"
        style="color: {ACCENT}"
      >
        Ways to help
      </p>
      <h2 class="text-[28px] font-bold tracking-tight">
        Seven ways to contribute
      </h2>
    </div>
    <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
      {#each LANES as lane}
        <a
          href={resolveHref(lane.href)}
          target={lane.external ? "_blank" : undefined}
          rel={lane.external ? "noopener noreferrer" : undefined}
          class="gi-lane-card flex flex-row items-start gap-4 bg-card border border-border rounded-xl p-5 transition-[border-color,transform] duration-200 no-underline text-inherit"
          class:gi-lane-highlight={lane.highlight}
          style="--lane-accent: {lane.accent}"
        >
          <div
            class="w-[42px] h-[42px] rounded-[11px] grid place-items-center shrink-0"
            style="background: color-mix(in oklch, {lane.accent}, transparent 85%)"
          >
            <lane.icon class="w-[22px] h-[22px]" color={lane.accent} />
          </div>
          <div class="flex-1 flex flex-col">
            <h3 class="text-lg font-semibold mb-1.5 tracking-tight">
              {lane.title}
            </h3>
            <p class="text-sm text-muted-foreground leading-relaxed flex-1">
              {lane.desc}
            </p>
            <span
              class="gi-lane-link inline-flex items-center gap-1.5 mt-3 text-sm font-semibold"
              style="color: {lane.accent}"
            >
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
        <p
          class="text-xs font-semibold tracking-[0.1em] uppercase m-0 mb-3.5"
          style="color: {ACCENT}"
        >
          Open tasks right now
        </p>
        <h2 class="text-[28px] font-bold tracking-tight m-0">
          Tagged <code class="text-xl">get-involved</code>
        </h2>
      </div>
      <a
        href={LINKS.githubLabel}
        target="_blank"
        rel="noopener noreferrer"
        class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-sm h-[38px] px-4 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 bg-transparent border border-border text-foreground hover:bg-accent"
      >
        Open the tracker <ExternalLink class="w-3.5 h-3.5" />
      </a>
    </div>

    <!-- Issue feed card -->
    <div class="border border-border rounded-[14px] bg-card overflow-hidden">
      <div
        class="flex items-center gap-3 px-[22px] py-[18px] border-b border-border"
        style="background: color-mix(in oklch, var(--card), var(--background) 35%)"
      >
        <img
          src="/logos/github.png"
          alt="GitHub"
          class="w-[22px] h-[22px] rounded-[5px] object-contain"
          onerror={(e) => { e.currentTarget.style.display = 'none'; }}
        />
        <span class="font-mono text-[13px] text-muted-foreground">nightscout/nocturne</span>
        <span
          class="inline-flex items-center gap-1.5 whitespace-nowrap text-xs font-semibold py-1 px-2.5 rounded-full ml-auto"
          style="background: color-mix(in oklch, {ACCENT}, transparent 85%); color: {ACCENT}; border: 1px solid color-mix(in oklch, {ACCENT}, transparent 70%)"
        >
          <Tag class="w-[13px] h-[13px]" color={ACCENT} />
          get-involved
        </span>
      </div>

      {#each ISSUES as issue}
        <a
          href={LINKS.githubLabel}
          target="_blank"
          rel="noopener noreferrer"
          class="gi-issue-row flex items-center gap-3.5 px-[18px] py-[13px] border-b border-border transition-[background] duration-150 cursor-pointer no-underline text-inherit hover:bg-[color-mix(in_oklch,var(--card),transparent_20%)] last:border-b-0"
        >
          <div class="gi-issue-dot shrink-0 w-3.5 h-3.5 rounded-full relative mt-[3px]" style="border: 2px solid {ACCENT}">
          </div>
          <div class="flex-1 min-w-0">
            <p class="text-[15px] font-semibold m-0 flex items-baseline gap-2 flex-wrap">
              {issue.title}
              <span class="font-mono text-[13px] font-medium text-muted-foreground">#{issue.num}</span>
            </p>
            <div class="flex items-center gap-2 flex-wrap mt-1.5">
              {#each issue.labels as label}
                {@const colors = LABEL_COLORS[label] || {
                  fg: "var(--muted-foreground)",
                  bg: "var(--muted)",
                }}
                <span
                  class="text-[11px] font-semibold tracking-[0.01em] whitespace-nowrap py-[3px] px-[9px] rounded-full"
                  style="color: {colors.fg}; background: {colors.bg}; border: 1px solid color-mix(in oklch, {colors.fg}, transparent 70%)"
                >
                  {label}
                </span>
              {/each}
            </div>
          </div>
          <div class="flex items-center gap-4 shrink-0 text-muted-foreground text-[13px]">
            <span class="inline-flex items-center gap-1.5">
              <MessageSquare class="w-3.5 h-3.5" /> {issue.comments}
            </span>
            <span>{issue.updated}</span>
          </div>
        </a>
      {/each}

      <div class="flex items-center justify-between gap-3 px-[22px] py-4">
        <span class="text-muted-foreground text-[13px]">Updated continuously — these are real, grabbable tasks.</span>
        <a
          href={LINKS.githubLabel}
          target="_blank"
          rel="noopener noreferrer"
          class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-sm h-[38px] px-4 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 bg-transparent border border-border text-foreground hover:bg-accent"
        >
          View all on GitHub <ExternalLink class="w-3.5 h-3.5" />
        </a>
      </div>
    </div>
  </section>

  <!-- Donate band -->
  <section class="pb-20" id="donate">
    <div
      class="rounded-[18px] p-10 flex items-center gap-8 flex-wrap"
      style="
        border: 1px solid color-mix(in oklch, {ACCENT}, transparent 55%);
        background:
          radial-gradient(120% 140% at 100% 0%, color-mix(in oklch, {ACCENT}, transparent 80%), transparent 60%),
          color-mix(in oklch, var(--card), transparent 30%);
      "
    >
      <div class="flex-1 min-w-[280px]">
        <h3 class="text-[26px] font-bold tracking-tight mb-2">
          Keep Nocturne free and independent
        </h3>
        <p class="text-muted-foreground m-0 max-w-[52ch]">
          There is no company behind Nocturne — just volunteers and the
          Nightscout Foundation, a registered non-profit. Donations cover
          servers, test devices, and the work that keeps your data yours.
        </p>
      </div>
      <div class="flex flex-col gap-2.5">
        <a
          href={LINKS.donate}
          target="_blank"
          rel="noopener noreferrer"
          class="inline-flex items-center justify-center gap-2 rounded-lg font-medium text-[15px] h-[46px] px-6 whitespace-nowrap no-underline cursor-pointer transition-all duration-150 text-white hover:brightness-108"
          style="background: {ACCENT}"
        >
          <HeartHandshake class="w-[17px] h-[17px]" /> Donate to the Foundation
        </a>
        <span class="text-xs text-muted-foreground text-center"
          >Tax-deductible in the US &middot; Supports the whole community</span
        >
      </div>
    </div>
  </section>
</div>

<style>
  .gi-accent-text {
    background: linear-gradient(118deg, var(--foreground), color-mix(in oklch, oklch(0.6 0.118 184.704), var(--foreground) 35%));
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
  }

  .gi-lane-card:hover {
    border-color: color-mix(in oklch, oklch(0.6 0.118 184.704), transparent 55%);
  }

  .gi-lane-highlight {
    border-color: color-mix(in oklch, oklch(0.6 0.118 184.704), transparent 45%);
    background: color-mix(in oklch, oklch(0.6 0.118 184.704), var(--card) 80%);
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
    background: oklch(0.6 0.118 184.704);
  }
</style>
