<script lang="ts">
  import { Check, Sprout, Cable } from "lucide-svelte";

  let {
    currentPath,
    onSelect,
  }: {
    currentPath: "fresh" | "migration";
    onSelect: (path: "fresh" | "migration") => void;
  } = $props();

  let freshSelected = $derived(currentPath === "fresh");
  let migrationSelected = $derived(currentPath === "migration");
</script>

<div class="flex flex-col items-center gap-10 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-brand font-hairline leading-tight tracking-tight text-white text-3xl md:text-4xl xl:text-5xl"
    >
      How are you <em class="not-italic font-light text-(--onb-green)">arriving</em>?
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-white/50">
      Both roads end in the same place. We just want to know whether to carry
      your existing Nightscout data over, or give you a clean notebook to start
      in.
    </p>
  </div>

  <!-- Path cards -->
  <div class="grid w-full max-w-170 grid-cols-1 gap-5 sm:grid-cols-2">
    <!-- Fresh Start -->
    <button
      class="group relative flex cursor-pointer flex-col gap-5 rounded-2xl border p-6 text-left transition-all duration-200 {freshSelected
        ? 'border-(--onb-green) bg-(--onb-green-dim)'
        : 'border-white/8 bg-white/3 hover:border-white/18 hover:-translate-y-0.5'}"
      style="--card-accent: var(--onb-green); --card-glow: var(--onb-green-dim);"
      onclick={() => onSelect("fresh")}
    >
      <!-- Radio dot -->
      <span
        class="absolute right-4 top-4 block h-5.5 w-5.5 rounded-full border-2 transition-all duration-200 {freshSelected
          ? 'border-(--card-accent) bg-(--card-accent) inset-ring-4 inset-ring-(--onb-navy)'
          : 'border-white/20'}"
      ></span>

      <!-- Tag pill -->
      <span
        class="inline-flex self-start rounded-full px-2.5 py-0.75 font-mono text-2xs uppercase tracking-wider bg-(--card-glow) text-(--card-accent)"
      >
        Fresh start
      </span>

      <!-- Icon -->
      <div
        class="flex h-12 w-12 items-center justify-center rounded-xl bg-(--card-glow)"
      >
        <Sprout class="h-6 w-6 text-(--card-accent)" />
      </div>

      <!-- Title -->
      <h2 class="font-brand text-xl font-normal leading-snug text-white">
        Start with a blank slate
      </h2>

      <!-- Description -->
      <p class="text-sm leading-relaxed text-white/50">
        I'm new to open-source diabetes data, or I'd rather not bring old data
        with me.
      </p>

      <!-- Bullets -->
      <ul class="flex flex-col gap-2.5">
        <li class="flex items-start gap-2.5 text-sm text-white/70">
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-(--card-accent)" />
          Pick your glucose units and target range
        </li>
        <li class="flex items-start gap-2.5 text-sm text-white/70">
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-(--card-accent)" />
          Connect a CGM, pump, or uploader
        </li>
        <li class="flex items-start gap-2.5 text-sm text-white/70">
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-(--card-accent)" />
          Land on a dashboard in about two minutes
        </li>
      </ul>

      <!-- Bottom glow streak -->
      <div
        class="card-streak pointer-events-none absolute bottom-0 left-1/2 h-px w-[70%] -translate-x-1/2 transition-opacity duration-300 {freshSelected
          ? 'opacity-100'
          : 'opacity-0'}"
      ></div>
    </button>

    <!-- Migration -->
    <button
      class="group relative flex cursor-pointer flex-col gap-5 rounded-2xl border p-6 text-left transition-all duration-200 {migrationSelected
        ? 'border-(--onb-lavender) bg-(--onb-lavender-dim)'
        : 'border-white/8 bg-white/3 hover:border-white/18 hover:-translate-y-0.5'}"
      style="--card-accent: var(--onb-lavender); --card-glow: var(--onb-lavender-dim);"
      onclick={() => onSelect("migration")}
    >
      <!-- Radio dot -->
      <span
        class="absolute right-4 top-4 block h-5.5 w-5.5 rounded-full border-2 transition-all duration-200 {migrationSelected
          ? 'border-(--card-accent) bg-(--card-accent) inset-ring-4 inset-ring-(--onb-navy)'
          : 'border-white/20'}"
      ></span>

      <!-- Tag pill -->
      <span
        class="inline-flex self-start rounded-full px-2.5 py-0.75 font-mono text-2xs uppercase tracking-wider bg-(--card-glow) text-(--card-accent)"
      >
        Coming from Nightscout
      </span>

      <!-- Icon -->
      <div
        class="flex h-12 w-12 items-center justify-center rounded-xl bg-(--card-glow)"
      >
        <Cable class="h-6 w-6 text-(--card-accent)" />
      </div>

      <!-- Title -->
      <h2 class="font-brand text-xl font-normal leading-snug text-white">
        Migrate my Nightscout data
      </h2>

      <!-- Description -->
      <p class="text-sm leading-relaxed text-white/50">
        I already run Nightscout. Pull my history across and keep my uploaders
        working.
      </p>

      <!-- Bullets -->
      <ul class="flex flex-col gap-2.5">
        <li class="flex items-start gap-2.5 text-sm text-white/70">
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-(--card-accent)" />
          Point at your existing Nightscout URL
        </li>
        <li class="flex items-start gap-2.5 text-sm text-white/70">
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-(--card-accent)" />
          Import entries, treatments, profiles
        </li>
        <li class="flex items-start gap-2.5 text-sm text-white/70">
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-(--card-accent)" />
          Run side-by-side during the switchover
        </li>
      </ul>

      <!-- Bottom glow streak -->
      <div
        class="card-streak pointer-events-none absolute bottom-0 left-1/2 h-px w-[70%] -translate-x-1/2 transition-opacity duration-300 {migrationSelected
          ? 'opacity-100'
          : 'opacity-0'}"
      ></div>
    </button>
  </div>
</div>

<style>
  .card-streak {
    background: radial-gradient(ellipse at center, var(--card-accent), transparent 70%);
    box-shadow: 0 0 20px 4px var(--card-glow);
  }
</style>
