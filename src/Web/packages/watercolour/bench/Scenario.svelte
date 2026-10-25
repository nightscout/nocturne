<script lang="ts">
  import { Artwork, ArtworkHero, AvatarWash, ConfirmationBackground, DropGroup, DropSurface, PaintedUnderline, ReportHeaderWash, SelectionEdge } from '../src';
  import type { ArtworkId } from '../src';
  import { expectArtworks, track } from './hooks';

  let { scenario, n }: { scenario: string; n?: number } = $props();

  const EMPTY_ART: ArtworkId[] = ['report-pages', 'magnifying-glass', 'calendar', 'moonlit-shoreline', 'key', 'heart-rate'];
  const NAV = ['Overview', 'Reports', 'Treatments', 'Devices', 'Members', 'Settings'];
  const TABS = ['Glucose', 'Insulin', 'Carbs', 'Activity', 'Sleep', 'Notes', 'Devices', 'Trends'];

  const count = (fallback: number) => n ?? fallback;
  const list = (k: number) => Array.from({ length: k }, (_, i) => i);

  let tab = $state(0);
  let nav = $state(0);

  const initial = {
    avatars: () => count(40),
    tabs: () => 1,
    empty: () => count(3) + 1,
    hero: () => 1,
    drops: () => 0,
    wash: () => 1,
    edge: () => 1,
    mixed: () => 1 + 1 + 10 + 2 + 1,
  } as Record<string, () => number>;

  expectArtworks(scenario, (initial[scenario] ?? (() => 0))());
</script>

{#snippet avatars(k: number)}
  <div class="avatars">
    {#each list(k) as i (i)}
      <div class="member">
        <AvatarWash name="Member {i}" size={48} onready={track('avatar')} />
        <span>Member {i}</span>
      </div>
    {/each}
  </div>
{/snippet}

{#snippet tabs(k: number)}
  <div class="tabbar" role="tablist">
    {#each list(k) as i (i)}
      <button class="tab" role="tab" data-bench-tab aria-selected={tab === i} onclick={() => (tab = i)}>
        {TABS[i % TABS.length]}
        <PaintedUnderline active={tab === i} seed={i + 1} onready={track('tab')} />
      </button>
    {/each}
  </div>
{/snippet}

{#snippet empties(k: number)}
  <div class="empties">
    {#each list(k) as i (i)}
      <div class="empty">
        <div class="art"><Artwork artwork={EMPTY_ART[i % EMPTY_ART.length]} seed={i + 1} class="h-full w-full" onready={track('empty')} /></div>
        <strong>Nothing here yet</strong>
        <p>Entries will appear once your device syncs.</p>
      </div>
    {/each}
  </div>
{/snippet}

{#snippet confirm()}
  <div class="confirm">
    <ConfirmationBackground onready={track('confirmation')} />
    <p style="position: relative; padding: 24px">Saved. Your changes are live.</p>
  </div>
{/snippet}

{#snippet edges(k: number)}
  <nav class="sidebar">
    {#each list(k) as i (i)}
      <button class="nav" data-bench-edge onclick={() => (nav = i)}>
        {NAV[i % NAV.length]}
        <SelectionEdge active={nav === i} seed={i + 1} onready={track('edge')} />
      </button>
    {/each}
  </nav>
{/snippet}

<div class="page" data-scenario={scenario}>
  {#if scenario === 'avatars'}
    {@render avatars(count(40))}
  {:else if scenario === 'tabs'}
    {@render tabs(count(5))}
  {:else if scenario === 'empty'}
    {@render empties(count(3))}
    {@render confirm()}
  {:else if scenario === 'hero'}
    <div class="hero"><ArtworkHero artwork="moonlit-shoreline" size={800} onready={track('hero')} /></div>
  {:else if scenario === 'drops'}
    <DropGroup name="bench">
      <div class="drops">
        {#each list(count(8)) as i (i)}
          <DropSurface name="Card {i}" class="drop" contentClass="" >
            <span data-bench-drop>Card {i}<br />Hover for the mark</span>
          </DropSurface>
        {/each}
      </div>
    </DropGroup>
  {:else if scenario === 'wash'}
    <ReportHeaderWash name="/reports/agp" class="header-wash" onready={track('wash')} />
  {:else if scenario === 'edge'}
    {@render edges(count(8))}
  {:else if scenario === 'mixed'}
    <div class="shell">
      {@render edges(6)}
      <div class="main">
        <ReportHeaderWash name="/reports/agp" class="header-wash" onready={track('wash')} />
        {@render tabs(5)}
        <h3>Members</h3>
        {@render avatars(10)}
        <h3>Empty</h3>
        {@render empties(2)}
      </div>
    </div>
  {:else}
    <p>Unknown scenario {scenario}</p>
  {/if}
</div>
