<script lang="ts">
  import * as Card from '@nocturne/ui/ui/card';
  import * as Select from '@nocturne/ui/ui/select';
  import * as ToggleGroup from '@nocturne/ui/ui/toggle-group';
  import { Button } from '@nocturne/ui/ui/button';
  import { Label } from '@nocturne/ui/ui/label';
  import { Slider } from '@nocturne/ui/ui/slider';
  import { Switch } from '@nocturne/ui/ui/switch';
  import Server from '@lucide/svelte/icons/server';
  import Database from '@lucide/svelte/icons/database';
  import Bell from '@lucide/svelte/icons/bell';
  import Users from '@lucide/svelte/icons/users';
  import Shield from '@lucide/svelte/icons/shield';
  import Heart from '@lucide/svelte/icons/heart';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import Play from '@lucide/svelte/icons/play';
  import ChevronRight from '@lucide/svelte/icons/chevron-right';
  import type { Component } from 'svelte';
  import PageHeader from '$lib/components/PageHeader.svelte';
  import PreviewSurface from '$lib/components/PreviewSurface.svelte';
  import {
    DEFAULT_MAX_LIVE_INSTANCES,
    DropGroup,
    DropSurface,
    PALETTE_IDS,
    getEngineHost,
  } from '$lib/artwork';
  import type { DropDeposit, DropKind, DropFonts, PaletteId } from '$lib/artwork';

  interface Feature {
    icon: Component;
    title: string;
    copy: string;
  }

  const FEATURES: readonly Feature[] = [
    { icon: Server, title: 'Your server, your data', copy: 'Self-hosted, with no cloud middleman in the way.' },
    { icon: Database, title: 'Built for years of data', copy: 'PostgreSQL underneath, queried for the long run.' },
    { icon: Bell, title: 'Alarms that reach you', copy: 'Push, email, or a bot in the chat you already use.' },
    { icon: Users, title: 'One install, many people', copy: 'A household or a clinic on one deployment.' },
    { icon: Shield, title: 'Free and open source', copy: 'AGPL-3.0 licensed, stewarded by the Foundation.' },
    { icon: Heart, title: 'Built by the community', copy: 'Volunteers, everywhere, scratching their own itch.' },
  ];

  const ACTIONS = ['Connect a device', 'Invite someone', 'Export a report'] as const;

  const ROWS = [
    { name: 'Dexcom G7', detail: 'Connected, last reading 4 minutes ago' },
    { name: 'Omnipod 5', detail: 'Connected, last upload 11 minutes ago' },
    { name: 'Libre 3', detail: 'Needs re-authorisation' },
  ] as const;

  /**
   * The fonts Pretext measures with.
   *
   * Each has to match the CSS on the element it is named for. `text-sm` is
   * 14 px on a 20 px line, and the body face is the theme's. A stack that does
   * not match returns line boxes for text that was never drawn.
   */
  const SANS = '"Cabin", sans-serif';
  const CARD_FONTS: DropFonts = {
    title: { font: `600 14px ${SANS}`, lineHeight: 20 },
    copy: { font: `400 14px ${SANS}`, lineHeight: 20 },
  };
  const ROW_FONTS: DropFonts = {
    name: { font: `500 14px ${SANS}`, lineHeight: 20 },
    detail: { font: `400 12px ${SANS}`, lineHeight: 16 },
  };

  /** The two deposits, in the order they are offered and compared. */
  const COMPARISON = [
    { key: 'wet', label: 'Wet into wet' },
    { key: 'stamp', label: 'Pigment alone' },
  ] as const;

  let hold = $state(false);
  let measured = $state(true);
  let themeColour = $state<PaletteId>('water');
  let tintSurface = $state(false);
  let deposit = $state<DropDeposit>('wet');
  let kind = $state<DropKind | 'auto'>('auto');
  const KINDS: readonly { key: DropKind | 'auto'; label: string }[] = [
    { key: 'auto', label: 'Cycle' },
    { key: 'stroke', label: 'Stroke' },
    { key: 'drops', label: 'Drops' },
    { key: 'splotch', label: 'Splotch' },
    { key: 'border', label: 'Border' },
  ];
  let spatter = $state(true);
  let generation = $state(0);
  let resolved = $state(new Map<string, string>());

  let scrub = $state(0.42);
  let playing = $state(false);
  let playGeneration = $state(0);

  const palette = $derived(themeColour);
  const fonts = $derived(measured ? CARD_FONTS : undefined);
  const rowFonts = $derived(measured ? ROW_FONTS : undefined);
  // Read off the resolved rows: the host has no stats until an engine exists,
  // and there is no event to watch for one appearing.
  const leases = $derived(resolved.size >= 0 ? (getEngineHost().stats()?.liveInstances ?? 0) : 0);

  function record(key: string, value: string) {
    resolved = new Map(resolved).set(key, value);
  }

  function repaint() {
    resolved = new Map();
    generation += 1;
  }

  /**
   * The first engine acquire blocks the main thread for a few hundred ms while
   * the wasm module loads and WebGPU hands over a device. Paid on the first
   * pointer-enter, it freezes the transition that pointer just started, so it
   * is paid here instead. A machine with no GPU reports false and shows no
   * mark, which is what it would have shown anyway.
   */
  /**
   * This page holds every surface open at once to compare them, which the
   * production cap of four would leave mostly blank. The cap is raised here
   * only and put back when the page goes.
   */
  const SHOWCASE_LIVE_INSTANCES = 16;

  $effect(() => {
    const host = getEngineHost();
    const production = host.maxLiveInstances;
    host.maxLiveInstances = SHOWCASE_LIVE_INSTANCES;
    void host.warm();
    return () => {
      host.maxLiveInstances = production;
    };
  });

  function play() {
    playing = false;
    playGeneration += 1;
    requestAnimationFrame(() => requestAnimationFrame(() => (playing = true)));
  }
</script>

<svelte:head>
  <title>Paint drops - Watercolour showcase</title>
</svelte:head>

<PageHeader
  title="Paint drops"
  description="Abstract marks placed in a surface's free space on hover, painted by the engine rather than served as stills."
/>

<div class="grid gap-6">
  <Card.Root>
    <Card.Header>
      <Card.Title>Controls</Card.Title>
      <Card.Description>
        A mark is one gesture fitted to the empty space. A stroke or a border is drawn along its path, then
        the ink spreads into the paper; drops and splotches land whole. In production the engine allows
        {DEFAULT_MAX_LIVE_INSTANCES} at once; this page raises that so every surface can be held open. A machine
        without WebGPU sees the surface without a mark.
      </Card.Description>
    </Card.Header>
    <Card.Content class="grid gap-4">
      <div class="flex flex-wrap items-center gap-6">
        <div class="flex items-center gap-2">
          <Switch id="drops-hold" checked={hold} onCheckedChange={(v) => (hold = v)} />
          <Label for="drops-hold">Hold everything</Label>
        </div>
        <div class="flex items-center gap-2">
          <Switch
            id="drops-measured"
            checked={measured}
            onCheckedChange={(v) => { measured = v; repaint(); }}
          />
          <Label for="drops-measured">Measure off the DOM</Label>
        </div>
        <div class="flex items-center gap-2">
          <Switch id="drops-tint" checked={tintSurface} onCheckedChange={(v) => (tintSurface = v)} />
          <Label for="drops-tint">Tint the surface</Label>
        </div>
        <div class="flex items-center gap-2">
          <Switch
            id="drops-spatter"
            checked={spatter}
            onCheckedChange={(v) => { spatter = v; repaint(); }}
          />
          <Label for="drops-spatter">Spatter</Label>
        </div>
        <Button variant="outline" size="sm" onclick={repaint}><RotateCcw /> Repaint</Button>
        <p class="font-mono text-xs text-muted-foreground">
          live instances {leases} / {SHOWCASE_LIVE_INSTANCES} (production cap {DEFAULT_MAX_LIVE_INSTANCES})
        </p>
      </div>
      <div class="flex flex-wrap items-end gap-6">
        <div class="grid gap-1">
          <span class="text-xs font-medium text-muted-foreground">Deposit</span>
          <ToggleGroup.Root
            type="single"
            variant="outline"
            size="sm"
            value={deposit}
            onValueChange={(v) => { if (v) { deposit = v as DropDeposit; repaint(); } }}
            aria-label="Deposit"
            class="justify-start"
          >
            {#each COMPARISON as option (option.key)}
              <ToggleGroup.Item value={option.key} aria-label={option.label}>{option.label}</ToggleGroup.Item>
            {/each}
          </ToggleGroup.Root>
        </div>
        <div class="grid gap-1">
          <span class="text-xs font-medium text-muted-foreground">Gesture</span>
          <ToggleGroup.Root
            type="single"
            variant="outline"
            size="sm"
            value={kind}
            onValueChange={(v) => { if (v) { kind = v as DropKind | 'auto'; repaint(); } }}
            aria-label="Gesture"
            class="justify-start"
          >
            {#each KINDS as option (option.key)}
              <ToggleGroup.Item value={option.key} aria-label={option.label}>{option.label}</ToggleGroup.Item>
            {/each}
          </ToggleGroup.Root>
        </div>
        <div class="grid gap-1">
          <span class="text-xs font-medium text-muted-foreground">Theme colour</span>
          <Select.Root
            type="single"
            value={themeColour}
            onValueChange={(v) => { themeColour = v as PaletteId; repaint(); }}
          >
            <Select.Trigger class="w-44" aria-label="Theme colour">{themeColour}</Select.Trigger>
            <Select.Content>
              {#each PALETTE_IDS as p (p)}
                <Select.Item value={p} label={p}>{p}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      </div>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>How it arrives</Card.Title>
      <Card.Description>
        The same stroke deposited two ways on the same card, pinned at one instant by the slider and played
        together from the button.
      </Card.Description>
    </Card.Header>
    <Card.Content class="grid gap-4">
      <div class="flex flex-wrap items-center gap-4">
        <Button variant="outline" size="sm" onclick={play}><Play /> Play</Button>
        <div class="flex min-w-64 flex-1 items-center gap-3">
          <Slider type="single" bind:value={scrub} min={0} max={1} step={0.01} aria-label="Reveal progress" />
          <span class="w-12 shrink-0 text-right font-mono text-xs text-muted-foreground">
            {Math.round(scrub * 100)} %
          </span>
        </div>
      </div>
      <PreviewSurface>
        <div class="grid gap-4 sm:grid-cols-2">
          {#each COMPARISON as column (column.key)}
            <div class="grid gap-2">
              <p class="text-xs font-medium text-muted-foreground">{column.label}</p>
              {#key playGeneration}
                <DropSurface
                  deposit={column.key}
                  index={1}
                  name="compare-{column.key}"
                  fonts={CARD_FONTS}
                  progress={playing ? undefined : scrub}
                  shown={playing ? true : undefined}
                  {palette}
                  {tintSurface}
                  onresolved={record}
                  class="rounded-xl border bg-card"
                  contentClass="flex items-start gap-3 p-4"
                >
                  <div
                    data-drop-obstacle
                    class="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary"
                  >
                    <Server class="size-5" />
                  </div>
                  <div class="min-w-0">
                    <h3 data-drop-text="title" class="text-sm font-semibold">Your server, your data</h3>
                    <p data-drop-text="copy" class="mt-0.5 text-sm text-muted-foreground">
                      Self-hosted, with no cloud middleman in the way.
                    </p>
                  </div>
                </DropSurface>
              {/key}
            </div>
          {/each}
        </div>
      </PreviewSurface>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Feature cards</Card.Title>
      <Card.Description>
        The case the marks were designed for: a wide hitbox, a glyph, two lines of copy and real empty space beside
        them. Consecutive cards never lead with the same mark in the same place.
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <PreviewSurface>
        <DropGroup name="feature cards">
          <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
            {#each FEATURES as feature (feature.title)}
              {@const Icon = feature.icon}
              <DropSurface
                {generation}
                {deposit}
                {kind}
                {spatter}
                {palette}
                {tintSurface}
                {fonts}
                name={feature.title}
                shown={hold ? true : undefined}
                onresolved={record}
                class="rounded-xl border bg-card transition-shadow hover:shadow-sm"
                contentClass="flex items-start gap-3 p-4"
              >
                <div
                  data-drop-obstacle
                  class="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary"
                >
                  <Icon class="size-5" />
                </div>
                <div class="min-w-0">
                  <h3 data-drop-text="title" class="text-sm font-semibold">{feature.title}</h3>
                  <p data-drop-text="copy" class="mt-0.5 text-sm text-muted-foreground">{feature.copy}</p>
                </div>
              </DropSurface>
            {/each}
          </div>
        </DropGroup>
      </PreviewSurface>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Buttons</Card.Title>
      <Card.Description>
        A button is nearly all label, so there is no empty space for a mark to find. A wash fits the stroke with the
        label ignored, behind it, while the other lets the stroke find the space beside the label or nothing at all.
      </Card.Description>
    </Card.Header>
    <Card.Content class="grid gap-4">
      <PreviewSurface>
        <div class="grid gap-4">
          <div class="grid gap-2">
            <p class="text-xs font-medium text-muted-foreground">Washed</p>
            <DropGroup name="wash buttons">
              <div class="flex flex-wrap gap-3">
                {#each ACTIONS as action (action)}
                  <DropSurface
                    {generation}
                    {deposit}
                    kind="glaze"
                    {spatter}
                    {palette}
                    {tintSurface}
                    name="glaze {action}"
                    shown={hold ? true : undefined}
                    onresolved={record}
                    class="rounded-md border bg-background"
                  >
                    <Button variant="ghost" size="lg" class="hover:bg-transparent">{action}</Button>
                  </DropSurface>
                {/each}
              </div>
            </DropGroup>
          </div>
          <div class="grid gap-2">
            <p class="text-xs font-medium text-muted-foreground">Taking an edge</p>
            <DropGroup name="edge buttons">
              <div class="flex flex-wrap gap-3">
                {#each ACTIONS as action (action)}
                  <DropSurface
                    {generation}
                    {deposit}
                    {kind}
                    {spatter}
                    {palette}
                    {tintSurface}
                    name="edge {action}"
                    shown={hold ? true : undefined}
                    onresolved={record}
                    class="rounded-md border bg-background"
                  >
                    <Button variant="ghost" size="lg" class="hover:bg-transparent">{action}</Button>
                  </DropSurface>
                {/each}
              </div>
            </DropGroup>
          </div>
        </div>
      </PreviewSurface>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>List rows</Card.Title>
      <Card.Description>
        A run of identical rows is where repetition shows worst. A row this tight has room for one stroke, and
        consecutive rows differ by their seed.
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <PreviewSurface>
        <DropGroup name="device rows">
          <div class="divide-y rounded-lg border">
            {#each ROWS as row (row.name)}
              <DropSurface
                {generation}
                {deposit}
                {kind}
                {spatter}
                {palette}
                {tintSurface}
                fonts={rowFonts}
                name={row.name}
                shown={hold ? true : undefined}
                onresolved={record}
                contentClass="flex items-center justify-between gap-3 px-4 py-3"
              >
                <div class="min-w-0">
                  <p data-drop-text="name" class="text-sm font-medium">{row.name}</p>
                  <p data-drop-text="detail" class="text-xs text-muted-foreground">{row.detail}</p>
                </div>
                <ChevronRight data-drop-obstacle class="size-4 shrink-0 text-muted-foreground" />
              </DropSurface>
            {/each}
          </div>
        </DropGroup>
      </PreviewSurface>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Surfaces that blobbed</Card.Title>
      <Card.Description>
        Three surface shapes on which the previous catalogue marks read as a disc with a boundary rather than
        paint, kept here so the stroke is judged where it used to fail.
      </Card.Description>
    </Card.Header>
    <Card.Content class="grid gap-6">
      {#snippet blobbed()}
        <div class="grid items-start gap-4 sm:grid-cols-3">
          <DropSurface
            {deposit}
            {kind}
            {spatter}
            {palette}
            {tintSurface}
            {generation}
            shown={hold ? true : undefined}
            onresolved={record}
            fonts={CARD_FONTS}
            name="tier"
            class="w-[245px] rounded-xl border bg-card"
            contentClass="flex min-h-[205px] flex-col gap-3 p-5"
          >
            <h3 data-drop-text="title" class="text-sm font-semibold">Supporter</h3>
            <p data-drop-text="copy" class="text-sm text-muted-foreground">
              Keep the servers running<br />and the data yours.
            </p>
            <Button variant="outline" size="sm" data-drop-obstacle class="mt-auto self-start">Choose</Button>
          </DropSurface>
          <DropSurface
            {deposit}
            {kind}
            {spatter}
            {palette}
            {tintSurface}
            {generation}
            shown={hold ? true : undefined}
            onresolved={record}
            fonts={CARD_FONTS}
            name="fork"
            class="w-[330px] rounded-xl border bg-card"
            contentClass="flex min-h-[330px] flex-col gap-4 p-6"
          >
            <div
              data-drop-obstacle
              class="flex size-12 items-center justify-center rounded-lg bg-primary/10 text-primary"
            >
              <Server class="size-6" />
            </div>
            <h3 data-drop-text="title" class="text-sm font-semibold">Connect a CGM or pump account</h3>
            <p data-drop-text="copy" class="text-sm text-muted-foreground">
              Dexcom, Medtronic, Libre,<br />Omnipod and more,<br />all in one place.
            </p>
          </DropSurface>
          <DropSurface
            {deposit}
            {kind}
            {spatter}
            {palette}
            {tintSurface}
            {generation}
            shown={hold ? true : undefined}
            onresolved={record}
            name="tile"
            class="w-[403px] rounded-lg border bg-card"
            contentClass="flex min-h-[142px] flex-col items-center gap-2 p-4 text-center"
          >
            <div
              data-drop-obstacle
              class="flex size-10 items-center justify-center rounded-lg bg-primary/10 text-primary"
            >
              <Heart class="size-5" />
            </div>
            <h3 data-drop-text="title" class="text-sm font-semibold">Sponsor the Foundation</h3>
            <p data-drop-text="copy" class="text-sm text-muted-foreground">Fund the work that keeps Nocturne free.</p>
          </DropSurface>
        </div>
      {/snippet}
      <PreviewSurface background="light">
        <DropGroup name="blobbed light">
          {@render blobbed()}
        </DropGroup>
      </PreviewSurface>
      <PreviewSurface background="dark">
        <DropGroup name="blobbed dark">
          {@render blobbed()}
        </DropGroup>
      </PreviewSurface>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>What resolved</Card.Title>
      <Card.Description>Each mark reports the backend it actually drew with, not the one it asked for.</Card.Description>
    </Card.Header>
    <Card.Content>
      {#if resolved.size === 0}
        <p class="text-sm text-muted-foreground">Hover a card, a button or a row.</p>
      {:else}
        <dl class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 font-mono text-xs">
          {#each [...resolved] as [key, value] (key)}
            <dt class="text-muted-foreground">{key}</dt>
            <dd>{value}</dd>
          {/each}
        </dl>
      {/if}
    </Card.Content>
  </Card.Root>
</div>
