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
      <span class="gi-eyebrow">
        <span class="gi-dot" style:background={ACCENT}></span>
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
        <a href="#tasks" class="gi-btn gi-btn-accent gi-btn-lg">
          Find a task <ArrowRight class="w-4 h-4" strokeWidth={2.5} />
        </a>
        <a
          href={LINKS.discord}
          target="_blank"
          rel="noopener noreferrer"
          class="gi-btn gi-btn-outline gi-btn-lg"
        >
          <MessageCircle class="w-4 h-4" /> Join the Discord
        </a>
      </div>
    </div>

    <!-- Stat widgets -->
    <div class="grid grid-cols-2 sm:grid-cols-4 gap-3 mt-10">
      {#each STATS as stat}
        <div class="gi-stat-widget">
          <p class="gi-stat-label">{stat.label}</p>
          <div class="gi-stat-value">{stat.value}</div>
        </div>
      {/each}
    </div>
  </section>

  <!-- Ways to help -->
  <section class="py-8" id="ways">
    <div class="mb-6">
      <p class="gi-section-eyebrow">Ways to help</p>
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
          class="gi-lane-card"
          class:gi-lane-highlight={lane.highlight}
        >
          <div
            class="gi-lane-icon"
            style:background="color-mix(in oklch, {lane.accent}, transparent 85%)"
          >
            <lane.icon class="w-[22px] h-[22px]" style:color={lane.accent} />
          </div>
          <div class="flex-1 flex flex-col">
            <h3 class="text-lg font-semibold mb-1.5 tracking-tight">
              {lane.title}
            </h3>
            <p class="text-sm text-muted-foreground leading-relaxed flex-1">
              {lane.desc}
            </p>
            <span
              class="gi-lane-link"
              style:color={lane.accent}
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
    <div
      class="mb-5 flex items-end justify-between gap-4 flex-wrap"
    >
      <div>
        <p class="gi-section-eyebrow">Open tasks right now</p>
        <h2 class="text-[28px] font-bold tracking-tight m-0">
          Tagged <code class="text-xl">get-involved</code>
        </h2>
      </div>
      <a
        href={LINKS.githubLabel}
        target="_blank"
        rel="noopener noreferrer"
        class="gi-btn gi-btn-outline"
      >
        Open the tracker <ExternalLink class="w-3.5 h-3.5" />
      </a>
    </div>

    <!-- Issue feed card -->
    <div class="gi-feed-card">
      <div class="gi-feed-head">
        <img
          src="/logos/github.png"
          alt="GitHub"
          class="w-[22px] h-[22px] rounded-[5px] object-contain"
          onerror="this.style.display='none'"
        />
        <span class="font-mono text-[13px] text-muted-foreground"
          >nightscout/nocturne</span
        >
        <span class="gi-tag-chip">
          <Tag class="w-[13px] h-[13px]" style:color={ACCENT} />
          get-involved
        </span>
      </div>

      {#each ISSUES as issue}
        <a
          href={LINKS.githubLabel}
          target="_blank"
          rel="noopener noreferrer"
          class="gi-issue-row"
        >
          <div class="gi-issue-dot"></div>
          <div class="flex-1 min-w-0">
            <p class="text-[15px] font-semibold m-0 flex items-baseline gap-2 flex-wrap">
              {issue.title}
              <span class="font-mono text-[13px] font-medium text-muted-foreground"
                >#{issue.num}</span
              >
            </p>
            <div class="flex items-center gap-2 flex-wrap mt-1.5">
              {#each issue.labels as label}
                {@const colors = LABEL_COLORS[label] || {
                  fg: "var(--muted-foreground)",
                  bg: "var(--muted)",
                }}
                <span
                  class="gi-label-pill"
                  style:color={colors.fg}
                  style:background={colors.bg}
                  style:border-color="color-mix(in oklch, {colors.fg}, transparent 70%)"
                >
                  {label}
                </span>
              {/each}
            </div>
          </div>
          <div
            class="flex items-center gap-4 shrink-0 text-muted-foreground text-[13px]"
          >
            <span class="inline-flex items-center gap-1.5">
              <MessageSquare class="w-3.5 h-3.5" /> {issue.comments}
            </span>
            <span>{issue.updated}</span>
          </div>
        </a>
      {/each}

      <div
        class="flex items-center justify-between gap-3 px-[22px] py-4"
      >
        <span class="text-muted-foreground text-[13px]"
          >Updated continuously — these are real, grabbable tasks.</span
        >
        <a
          href={LINKS.githubLabel}
          target="_blank"
          rel="noopener noreferrer"
          class="gi-btn gi-btn-outline"
        >
          View all on GitHub <ExternalLink class="w-3.5 h-3.5" />
        </a>
      </div>
    </div>
  </section>

  <!-- Donate band -->
  <section class="pb-20" id="donate">
    <div class="gi-donate-band">
      <div class="flex-1 min-w-[280px]">
        <h3
          class="text-[26px] font-bold tracking-tight mb-2"
        >
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
          class="gi-btn gi-btn-accent gi-btn-lg"
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
  /* ── Accent text gradient ─────────────────── */
  .gi-accent-text {
    background: linear-gradient(
      118deg,
      var(--foreground),
      color-mix(
        in oklch,
        oklch(0.6 0.118 184.704),
        var(--foreground) 35%
      )
    );
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
  }

  /* ── Eyebrow pill ─────────────────────────── */
  .gi-eyebrow {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    white-space: nowrap;
    font-size: 12px;
    font-weight: 500;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--muted-foreground);
    background: color-mix(in oklch, var(--card), transparent 50%);
    border: 1px solid var(--border);
    padding: 6px 12px;
    border-radius: 9999px;
    backdrop-filter: blur(6px);
  }

  .gi-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    display: inline-block;
  }

  /* ── Buttons ──────────────────────────────── */
  .gi-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    border-radius: 8px;
    font: inherit;
    font-weight: 500;
    font-size: 14px;
    height: 38px;
    padding: 0 16px;
    border: 1px solid transparent;
    cursor: pointer;
    transition: all 0.15s;
    white-space: nowrap;
    text-decoration: none;
  }

  .gi-btn-accent {
    background: oklch(0.6 0.118 184.704);
    color: oklch(0.99 0 0);
  }
  .gi-btn-accent:hover {
    filter: brightness(1.08);
  }

  .gi-btn-outline {
    background: transparent;
    border-color: var(--border);
    color: var(--foreground);
  }
  .gi-btn-outline:hover {
    background: var(--accent);
  }

  .gi-btn-lg {
    height: 46px;
    padding: 0 24px;
    font-size: 15px;
  }

  /* ── Section eyebrow ──────────────────────── */
  .gi-section-eyebrow {
    font-size: 12px;
    font-weight: 600;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: oklch(0.6 0.118 184.704);
    margin: 0 0 14px;
  }

  /* ── Stat widgets ─────────────────────────── */
  .gi-stat-widget {
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 20px;
  }

  .gi-stat-label {
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--muted-foreground);
    margin: 0 0 10px;
  }

  .gi-stat-value {
    font-size: 36px;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    letter-spacing: -0.03em;
    line-height: 1;
    color: var(--foreground);
  }

  /* ── Lane cards (dashboard flat) ──────────── */
  .gi-lane-card {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 16px;
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 20px;
    transition:
      border-color 0.2s,
      transform 0.2s;
    text-decoration: none;
    color: inherit;
  }

  .gi-lane-card:hover {
    border-color: color-mix(
      in oklch,
      oklch(0.6 0.118 184.704),
      transparent 55%
    );
  }

  .gi-lane-highlight {
    border-color: color-mix(
      in oklch,
      oklch(0.6 0.118 184.704),
      transparent 45%
    );
    background: color-mix(
      in oklch,
      oklch(0.6 0.118 184.704),
      var(--card) 80%
    );
  }

  .gi-lane-icon {
    width: 42px;
    height: 42px;
    border-radius: 11px;
    display: grid;
    place-items: center;
    flex-shrink: 0;
  }

  .gi-lane-link {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    margin-top: 12px;
    font-size: 14px;
    font-weight: 600;
  }

  .gi-lane-link :global(svg) {
    transition: transform 0.15s;
  }

  .gi-lane-card:hover .gi-lane-link :global(svg) {
    transform: translateX(3px);
  }

  /* ── Issue feed ───────────────────────────── */
  .gi-feed-card {
    border: 1px solid var(--border);
    border-radius: 14px;
    background: var(--card);
    overflow: hidden;
  }

  .gi-feed-head {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 18px 22px;
    border-bottom: 1px solid var(--border);
    background: color-mix(in oklch, var(--card), var(--background) 35%);
  }

  .gi-tag-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    white-space: nowrap;
    font-size: 12px;
    font-weight: 600;
    padding: 4px 10px;
    border-radius: 9999px;
    background: color-mix(
      in oklch,
      oklch(0.6 0.118 184.704),
      transparent 85%
    );
    color: oklch(0.6 0.118 184.704);
    border: 1px solid
      color-mix(in oklch, oklch(0.6 0.118 184.704), transparent 70%);
    margin-left: auto;
  }

  .gi-issue-row {
    display: flex;
    align-items: center;
    gap: 14px;
    padding: 13px 18px;
    border-bottom: 1px solid var(--border);
    transition: background 0.15s;
    cursor: pointer;
    text-decoration: none;
    color: inherit;
  }

  .gi-issue-row:last-of-type {
    border-bottom: none;
  }

  .gi-issue-row:hover {
    background: color-mix(in oklch, var(--card), transparent 20%);
  }

  .gi-issue-dot {
    flex-shrink: 0;
    width: 14px;
    height: 14px;
    border-radius: 50%;
    border: 2px solid oklch(0.6 0.118 184.704);
    position: relative;
    margin-top: 3px;
  }

  .gi-issue-dot::after {
    content: "";
    position: absolute;
    inset: 3px;
    border-radius: 50%;
    background: oklch(0.6 0.118 184.704);
  }

  .gi-label-pill {
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.01em;
    white-space: nowrap;
    padding: 3px 9px;
    border-radius: 9999px;
    border: 1px solid transparent;
  }

  /* ── Donate band ──────────────────────────── */
  .gi-donate-band {
    border: 1px solid
      color-mix(in oklch, oklch(0.6 0.118 184.704), transparent 55%);
    border-radius: 18px;
    background:
      radial-gradient(
        120% 140% at 100% 0%,
        color-mix(in oklch, oklch(0.6 0.118 184.704), transparent 80%),
        transparent 60%
      ),
      color-mix(in oklch, var(--card), transparent 30%);
    padding: 40px;
    display: flex;
    align-items: center;
    gap: 32px;
    flex-wrap: wrap;
  }
</style>
