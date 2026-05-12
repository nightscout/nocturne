<script lang="ts">
  import { Search, Check, Clock } from "@lucide/svelte";

  interface Connector {
    file: string;
    name: string;
    kind: string;
    aliases?: string[];
    comingSoon?: true;
    issue?: number;
  }

  const CONNECTORS: Connector[] = [
    {
      file: "dexcom.png",
      name: "Dexcom",
      kind: "CGM",
      aliases: ["Clarity", "g6", "g7"],
    },
    {
      file: "libre.png",
      name: "FreeStyle Libre",
      kind: "CGM",
      aliases: ["Libre", "FSL", "Abbott"],
    },
    { file: "glooko.png", name: "Glooko", kind: "Cloud" },
    { file: "medtronic.jpg", name: "Medtronic", kind: "Pump" },
    {
      file: "omnipod.png",
      name: "Omnipod",
      kind: "Pump",
      aliases: ["Insulet", "DASH", "5"],
    },
    { file: "loop.png", name: "Loop", kind: "Looping" },
    { file: "trio.jpg", name: "Trio", kind: "Looping" },
    {
      file: "aaps.png",
      name: "AndroidAPS",
      kind: "Looping",
      aliases: ["AAPS", "android"],
    },
    {
      file: "xdrip.jpg",
      name: "xDrip+",
      kind: "App",
      aliases: ["xDrip", "android"],
    },
    { file: "mylife.png", name: "myLife", kind: "Pump" },
    {
      file: "myfitnesspal.jpg",
      name: "MyFitnessPal",
      kind: "Food",
      aliases: ["MFP"],
    },
    { file: "tidepool.jpg", name: "Tidepool", kind: "Cloud" },
    { file: "sugarmate.png", name: "Sugarmate", kind: "App" },
    { file: "spike.png", name: "Spike", kind: "App" },
    { file: "juggluco.png", name: "Juggluco", kind: "App" },
    { file: "glucotracker.png", name: "GlucoTracker", kind: "App" },
    {
      file: "nightscout.png",
      name: "Nightscout",
      kind: "Legacy",
      aliases: ["NS"],
    },
    { file: "discord.png", name: "Discord", kind: "Notify" },
    { file: "slack.png", name: "Slack", kind: "Notify" },
    { file: "telegram.png", name: "Telegram", kind: "Notify" },
    {
      file: "home-assistant.png",
      name: "Home Assistant",
      kind: "Smart Home",
      aliases: ["HA", "smart home"],
    },
    // Coming soon
    {
      file: "tandem.png",
      name: "Tandem",
      kind: "Pump",
      aliases: ["TConnect"],
      comingSoon: true,
      issue: 72,
    },
    {
      file: "medtrum.jpg",
      name: "Medtrum",
      kind: "CGM",
      comingSoon: true,
      issue: 106,
    },
    {
      file: "n8n.png",
      name: "n8n",
      kind: "Cloud",
      comingSoon: true,
      issue: 112,
    },
    {
      file: "twilio.png",
      name: "Twilio / SMS",
      kind: "Messaging",
      aliases: ["SMS", "Twilio"],
      comingSoon: true,
      issue: 137,
    },
    {
      file: "oura.png",
      name: "Oura",
      kind: "App",
      aliases: ["Oura Ring"],
      comingSoon: true,
      issue: 151,
    },
    {
      file: "whatsapp.png",
      name: "WhatsApp",
      kind: "Messaging",
      aliases: ["WA"],
      comingSoon: true,
      issue: 177,
    },
    {
      file: "imessage.jpg",
      name: "iMessage",
      kind: "Messaging",
      aliases: ["Apple Messages", "iMsg", "ios"],
      comingSoon: true,
      issue: 178,
    },
    {
      file: "google-chat.png",
      name: "Google Chat",
      kind: "Messaging",
      aliases: ["GChat"],
      comingSoon: true,
      issue: 179,
    },
    {
      file: "teams.png",
      name: "MS Teams",
      kind: "Messaging",
      aliases: ["Teams", "Microsoft Teams"],
      comingSoon: true,
      issue: 180,
    },
    {
      file: "email.jpg",
      name: "Email",
      kind: "Notify",
      comingSoon: true,
      issue: 181,
    },
  ];

  const TYPE_TARGETS = ["Dexcom", "Libre", "Loop", "Omnipod", "xDrip"];

  interface Props {
    height?: number;
  }
  let { height = 340 }: Props = $props();

  let query = $state("");
  let userTyping = $state(false);

  $effect(() => {
    if (userTyping) return;
    let cancelled = false;
    let ti = 0;
    let i = 0;
    let timer: ReturnType<typeof setTimeout>;

    const tick = () => {
      if (cancelled) return;
      const word = TYPE_TARGETS[ti];
      if (i <= word.length) {
        query = word.slice(0, i);
        i++;
        timer = setTimeout(tick, 130);
      } else {
        timer = setTimeout(() => {
          if (cancelled) return;
          let j = word.length;
          const erase = () => {
            if (cancelled) return;
            if (j >= 0) {
              query = word.slice(0, j);
              j--;
              timer = setTimeout(erase, 60);
            } else {
              i = 0;
              ti = (ti + 1) % TYPE_TARGETS.length;
              timer = setTimeout(tick, 400);
            }
          };
          erase();
        }, 1600);
      }
    };
    tick();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  });

  function matches(c: Connector, q: string): boolean {
    const lower = q.toLowerCase();
    return (
      c.name.toLowerCase().includes(lower) ||
      c.kind.toLowerCase().includes(lower) ||
      (c.aliases?.some((a) => a.toLowerCase().includes(lower)) ?? false)
    );
  }

  let filtered = $derived(
    query ? CONNECTORS.filter((c) => matches(c, query)) : CONNECTORS,
  );

  let activeMatches = $derived(filtered.filter((c) => !c.comingSoon).length);
  let comingSoonMatches = $derived(filtered.filter((c) => c.comingSoon).length);
</script>

<div
  class="rounded-[14px] overflow-hidden border border-white/10 bg-[oklch(0.10_0.025_261)] flex flex-col"
  style:height="{height}px"
>
  <!-- Search bar -->
  <div
    class="px-[18px] py-3.5 border-b border-white/[0.08] flex items-center gap-3 bg-[oklch(0.12_0.025_261)] shrink-0"
  >
    <Search class="size-5 text-muted-foreground shrink-0" />
    <input
      class="flex-1 bg-transparent border-none outline-none text-[17px] text-foreground font-medium placeholder:text-muted-foreground/60 min-h-7 w-0"
      placeholder="Find your device or app…"
      aria-label="Search connectors"
      bind:value={query}
      onfocus={() => (userTyping = true)}
      onblur={() => {
        if (!query) userTyping = false;
      }}
      autocomplete="off"
      spellcheck="false"
    />
    {#if !userTyping}
      <span
        class="cursor-blink ml-0.5 inline-block w-0.5 h-5 bg-glucose-in-range align-middle pointer-events-none"
        aria-hidden="true"
      ></span>
    {/if}
    <span class="font-mono text-[12px] shrink-0 flex items-center gap-1.5">
      {#if query}
        <span class="text-glucose-in-range px-2.5 py-1 rounded-full bg-glucose-in-range/15 border border-glucose-in-range/30">
          {activeMatches} match{activeMatches === 1 ? "" : "es"}
        </span>
        {#if comingSoonMatches > 0}
          <span class="text-muted-foreground px-2.5 py-1 rounded-full bg-muted/30 border border-border">
            {comingSoonMatches} coming soon
          </span>
        {/if}
      {:else}
        <span class="text-glucose-in-range px-2.5 py-1 rounded-full bg-glucose-in-range/15 border border-glucose-in-range/30">
          {activeMatches} live
        </span>
        <span class="text-muted-foreground px-2.5 py-1 rounded-full bg-muted/30 border border-border">
          {comingSoonMatches} coming soon
        </span>
      {/if}
    </span>
  </div>

  <!-- Connector grid -->
  <div
    class="flex-1 p-3.5 overflow-y-auto grid content-start gap-2"
    style="grid-template-columns: repeat(auto-fill, minmax(130px, 1fr))"
  >
    {#each filtered as c (c.file)}
      {@const isMatch = !!query && matches(c, query)}
      <svelte:element
        this={c.comingSoon && c.issue ? 'a' : 'div'}
        href={c.comingSoon && c.issue ? `https://github.com/nightscout/nocturne/issues/${c.issue}` : undefined}
        target={c.comingSoon && c.issue ? "_blank" : undefined}
        rel={c.comingSoon && c.issue ? "noopener noreferrer" : undefined}
        class="flex items-center gap-2.5 px-3 py-2.5 rounded-lg border transition-all duration-200
                    {c.comingSoon
          ? 'opacity-50 bg-white/2 border-white/4 cursor-pointer hover:opacity-70'
          : isMatch
            ? 'bg-glucose-in-range/[0.14] border-glucose-in-range/50'
            : 'bg-white/4 border-white/6'}"
      >
        <img
          src="/logos/{c.file}"
          alt={c.name}
          class="size-5 rounded object-cover shrink-0 {c.comingSoon ? 'grayscale' : ''}"
          onerror={(e) => { (e.currentTarget as HTMLImageElement).src = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23ffffff30' stroke-width='1.5'%3E%3Crect x='3' y='3' width='18' height='18' rx='3'/%3E%3C/svg%3E"; }}
        />
        <span class="text-[13px] text-foreground font-medium truncate"
          >{c.name}</span
        >
        {#if c.comingSoon}
          <Clock class="size-3.5 text-muted-foreground shrink-0 ml-auto" />
        {:else if isMatch}
          <Check class="size-3.5 text-glucose-in-range shrink-0 ml-auto" />
        {/if}
      </svelte:element>
    {/each}
  </div>
</div>

<style>
  @keyframes blink {
    0%,
    100% {
      opacity: 1;
    }
    50% {
      opacity: 0;
    }
  }
  .cursor-blink {
    animation: blink 1s steps(2) infinite;
  }
</style>
