<script lang="ts">
  import * as Card from '@nocturne/ui/ui/card';
  import * as Select from '@nocturne/ui/ui/select';
  import * as ToggleGroup from '@nocturne/ui/ui/toggle-group';
  import { Button } from '@nocturne/ui/ui/button';
  import { Input } from '@nocturne/ui/ui/input';
  import { Label } from '@nocturne/ui/ui/label';
  import { Slider } from '@nocturne/ui/ui/slider';
  import { Switch } from '@nocturne/ui/ui/switch';
  import Play from '@lucide/svelte/icons/play';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import Server from '@lucide/svelte/icons/server';
  import { mode } from 'mode-watcher';
  import { DropSurface, PALETTE_IDS, getEngineHost } from '$lib/artwork';
  import type { DropDeposit, DropFonts, PaletteId, Surface } from '$lib/artwork';
  import type { DropKind } from '@nocturne/watercolour';
  import PreviewSurface from './PreviewSurface.svelte';

  /**
   * The surface sizes the gestures were tuned on, plus the tall card where a
   * splotch's circle is far wider than its clear band.
   */
  const PRESETS = [
    { key: 'card', label: 'Feature card', width: 364, height: 101 },
    { key: 'row', label: 'List row', width: 939, height: 61 },
    { key: 'tall', label: 'Tall card', width: 330, height: 330 },
    { key: 'tile', label: 'Tile', width: 403, height: 142 },
    { key: 'button', label: 'Button', width: 160, height: 40 },
  ] as const;

  const KINDS: readonly { key: DropKind; label: string }[] = [
    { key: 'stroke', label: 'Stroke' },
    { key: 'drops', label: 'Drops' },
    { key: 'splotch', label: 'Splotch' },
    { key: 'border', label: 'Border' },
    { key: 'wash', label: 'Wash' },
    { key: 'glaze', label: 'Glaze' },
  ];

  const DEPOSITS: readonly { key: DropDeposit; label: string }[] = [
    { key: 'wet', label: 'Wet into wet' },
    { key: 'stamp', label: 'Pigment alone' },
  ];

  /** Matches the copy's CSS, so the off-DOM measurement sees the lines that are drawn. */
  const SANS = '"Cabin", sans-serif';
  const FONTS: DropFonts = {
    title: { font: `600 14px ${SANS}`, lineHeight: 20 },
    copy: { font: `400 14px ${SANS}`, lineHeight: 20 },
  };

  let width = $state(364);
  let height = $state(101);
  let kind = $state<DropKind>('splotch');
  let deposit = $state<DropDeposit>('wet');
  let spatter = $state(true);
  /**
   * What sits in the surface for the mark to avoid. `narrow` breaks the copy
   * into short lines, which leaves the clear band a splotch needs beside it.
   */
  let copy = $state<'none' | 'wide' | 'narrow'>('wide');
  const COPY_OPTIONS = [
    { key: 'wide', label: 'Wide copy' },
    { key: 'narrow', label: 'Narrow copy' },
    { key: 'none', label: 'No copy' },
  ] as const;
  let palette = $state<PaletteId>('water');
  let surfaceMode = $state<'auto' | Surface>('auto');
  let seed = $state('playground');
  let intensity = $state(0.7);
  let generation = $state(0);
  let scrub = $state(1);
  let playing = $state(false);
  let playGeneration = $state(0);
  let resolved = $state('pending');
  let host: HTMLElement | undefined = $state();
  let readout = $state({ gesture: '-', frame: '-', backing: '-' });

  const surface = $derived<Surface>(surfaceMode === 'auto' ? (mode.current === 'dark' ? 'dark' : 'light') : surfaceMode);
  const preset = $derived(PRESETS.find((p) => p.width === width && p.height === height)?.key ?? 'custom');

  function usePreset(key: string) {
    const p = PRESETS.find((q) => q.key === key);
    if (!p) return;
    width = p.width;
    height = p.height;
    repaint();
  }

  function repaint() {
    resolved = 'pending';
    generation += 1;
  }

  function play() {
    playing = false;
    playGeneration += 1;
    requestAnimationFrame(() => requestAnimationFrame(() => (playing = true)));
  }

  function record(_: string, value: string) {
    resolved = value;
    requestAnimationFrame(measureReadout);
  }

  function measureReadout() {
    const drop = host?.querySelector<HTMLElement>('.nwc-drop');
    const canvas = drop?.querySelector('canvas');
    readout = {
      gesture: drop?.dataset.kind ?? 'none fits',
      frame: drop ? `${Math.round(drop.offsetWidth)} x ${Math.round(drop.offsetHeight)} CSS px` : '-',
      backing: canvas ? `${canvas.width} x ${canvas.height} px` : '-',
    };
  }

  $effect(() => {
    void getEngineHost().warm();
  });

  const clampSize = (v: string, fallback: number) => Math.min(1200, Math.max(24, Math.round(Number(v) || fallback)));
</script>

<div class="grid gap-6 lg:grid-cols-[minmax(0,320px)_minmax(0,1fr)]">
  <Card.Root class="self-start">
    <Card.Header>
      <Card.Title>Drop</Card.Title>
    </Card.Header>
    <Card.Content class="grid gap-5">
      <div class="grid gap-2">
        <Label>Gesture</Label>
        <ToggleGroup.Root
          type="single"
          variant="outline"
          size="sm"
          value={kind}
          onValueChange={(v) => { if (v) { kind = v as DropKind; repaint(); } }}
          aria-label="Gesture"
          class="flex-wrap justify-start"
        >
          {#each KINDS as option (option.key)}
            <ToggleGroup.Item value={option.key} aria-label={option.label}>{option.label}</ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
      </div>
      <div class="grid gap-2">
        <Label>Deposit</Label>
        <ToggleGroup.Root
          type="single"
          variant="outline"
          size="sm"
          value={deposit}
          onValueChange={(v) => { if (v) { deposit = v as DropDeposit; repaint(); } }}
          aria-label="Deposit"
          class="justify-start"
        >
          {#each DEPOSITS as option (option.key)}
            <ToggleGroup.Item value={option.key} aria-label={option.label}>{option.label}</ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
      </div>
      <div class="grid gap-2">
        <Label>Surface size</Label>
        <Select.Root type="single" value={preset} onValueChange={usePreset}>
          <Select.Trigger class="w-full" aria-label="Surface size">
            {PRESETS.find((p) => p.key === preset)?.label ?? 'Custom'}
          </Select.Trigger>
          <Select.Content>
            {#each PRESETS as p (p.key)}
              <Select.Item value={p.key} label={p.label}>{p.label} ({p.width} x {p.height})</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
        <div class="grid grid-cols-2 gap-3">
          <Input
            type="number"
            aria-label="Surface width"
            value={width}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => { width = clampSize(e.currentTarget.value, width); repaint(); }}
          />
          <Input
            type="number"
            aria-label="Surface height"
            value={height}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => { height = clampSize(e.currentTarget.value, height); repaint(); }}
          />
        </div>
      </div>
      <div class="grid gap-2">
        <Label>Content</Label>
        <ToggleGroup.Root
          type="single"
          variant="outline"
          size="sm"
          value={copy}
          onValueChange={(v) => { if (v) { copy = v as 'none' | 'wide' | 'narrow'; repaint(); } }}
          aria-label="Content"
          class="justify-start"
        >
          {#each COPY_OPTIONS as option (option.key)}
            <ToggleGroup.Item value={option.key} aria-label={option.label}>{option.label}</ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
      </div>
      <div class="flex flex-wrap gap-6">
        <div class="flex items-center gap-2">
          <Switch id="drop-spatter" checked={spatter} onCheckedChange={(v) => { spatter = v; repaint(); }} />
          <Label for="drop-spatter">Spatter</Label>
        </div>

      </div>
      <div class="grid grid-cols-2 gap-3">
        <div class="grid gap-2">
          <Label>Palette</Label>
          <Select.Root type="single" value={palette} onValueChange={(v) => { palette = v as PaletteId; repaint(); }}>
            <Select.Trigger class="w-full capitalize" aria-label="Palette">{palette}</Select.Trigger>
            <Select.Content>
              {#each PALETTE_IDS as p (p)}
                <Select.Item value={p} label={p} class="capitalize">{p}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="grid gap-2">
          <Label>Surface</Label>
          <Select.Root type="single" value={surfaceMode} onValueChange={(v) => { surfaceMode = v as 'auto' | Surface; repaint(); }}>
            <Select.Trigger class="w-full" aria-label="Surface">
              {surfaceMode === 'auto' ? `Auto (${surface})` : surfaceMode === 'dark' ? 'Dark' : 'Light'}
            </Select.Trigger>
            <Select.Content>
              <Select.Item value="auto" label="Auto (theme)">Auto (theme)</Select.Item>
              <Select.Item value="light" label="Light">Light</Select.Item>
              <Select.Item value="dark" label="Dark">Dark</Select.Item>
            </Select.Content>
          </Select.Root>
        </div>
      </div>
      <div class="grid grid-cols-2 gap-3">
        <div class="grid gap-2">
          <Label for="drop-seed">Seed</Label>
          <Input
            id="drop-seed"
            value={seed}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => { seed = e.currentTarget.value; repaint(); }}
          />
        </div>
        <div class="grid gap-2">
          <div class="flex justify-between text-sm">
            <Label>Intensity</Label><span class="tabular-nums text-muted-foreground">{Math.round(intensity * 100)}%</span>
          </div>
          <Slider
            type="single"
            value={intensity}
            onValueCommit={(v) => { intensity = v; repaint(); }}
            min={0}
            max={1}
            step={0.05}
            aria-label="Intensity"
          />
        </div>
      </div>
      <div class="grid gap-2">
        <Label>Replay</Label>
        <div class="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onclick={play}><Play /> Play</Button>
          <Button variant="outline" size="sm" onclick={repaint}><RotateCcw /> Repaint</Button>
        </div>
        <div class="flex items-center gap-3">
          <Slider type="single" bind:value={scrub} min={0} max={1} step={0.01} aria-label="Settle progress" />
          <span class="w-12 text-right text-sm tabular-nums text-muted-foreground">{Math.round(scrub * 100)}%</span>
        </div>
      </div>
    </Card.Content>
  </Card.Root>

  <Card.Root>
    <Card.Header>
      <Card.Title>Surface</Card.Title>
      <Card.Description>
        The shipped <code>DropSurface</code>, held open. The slider pins the settle; Play runs it at its real speed.
      </Card.Description>
    </Card.Header>
    <Card.Content class="grid gap-4">
      <PreviewSurface background={surface} class="overflow-auto">
        <div bind:this={host} class="flex min-h-40 items-center justify-center py-6">
          {#key `${generation}|${playGeneration}`}
            <DropSurface
              {kind}
              {deposit}
              {spatter}
              {palette}
              {surface}
              {intensity}
              {generation}
              name={seed}
              fonts={copy === 'none' ? undefined : FONTS}
              progress={playing ? undefined : scrub}
              shown={playing ? true : undefined}
              onresolved={record}
              class="shrink-0 rounded-xl border bg-card"
              contentClass="flex h-full items-start gap-3 p-4"
              style="width:{width}px; height:{height}px"
            >
              {#if copy !== 'none'}
                <div
                  data-drop-obstacle
                  class="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary"
                >
                  <Server class="size-5" />
                </div>
                <div class="min-w-0">
                  {#if copy === 'narrow'}
                    <h3 data-drop-text="title" class="text-sm font-semibold">Connect a CGM</h3>
                    <p data-drop-text="copy" class="mt-0.5 text-sm text-muted-foreground">
                      Dexcom, Medtronic,<br />Omnipod and more,<br />all in one place.
                    </p>
                  {:else}
                    <h3 data-drop-text="title" class="text-sm font-semibold">Your server, your data</h3>
                    <p data-drop-text="copy" class="mt-0.5 text-sm text-muted-foreground">
                      Self-hosted, with no cloud middleman in the way.
                    </p>
                  {/if}
                </div>
              {/if}
            </DropSurface>
          {/key}
        </div>
      </PreviewSurface>
      <dl class="grid grid-cols-2 gap-x-6 gap-y-1 font-mono text-xs sm:grid-cols-5">
        <div><dt class="text-muted-foreground">gesture</dt><dd>{readout.gesture}{readout.gesture !== kind && readout.gesture !== '-' ? ` (asked ${kind})` : ''}</dd></div>
        <div><dt class="text-muted-foreground">surface</dt><dd>{width} x {height} CSS px</dd></div>
        <div><dt class="text-muted-foreground">canvas</dt><dd>{readout.frame}</dd></div>
        <div><dt class="text-muted-foreground">backing</dt><dd>{readout.backing}</dd></div>
        <div><dt class="text-muted-foreground">backend</dt><dd>{resolved}</dd></div>
      </dl>
    </Card.Content>
  </Card.Root>
</div>
