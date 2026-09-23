<script lang="ts">
  import * as Card from '@nocturne/ui/ui/card';
  import * as Select from '@nocturne/ui/ui/select';
  import * as ToggleGroup from '@nocturne/ui/ui/toggle-group';
  import { Button } from '@nocturne/ui/ui/button';
  import { Input } from '@nocturne/ui/ui/input';
  import { Label } from '@nocturne/ui/ui/label';
  import { Slider } from '@nocturne/ui/ui/slider';
  import { Separator } from '@nocturne/ui/ui/separator';
  import { toast } from 'svelte-sonner';
  import Play from '@lucide/svelte/icons/play';
  import Pause from '@lucide/svelte/icons/pause';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';
  import SkipForward from '@lucide/svelte/icons/skip-forward';
  import Download from '@lucide/svelte/icons/download';
  import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
  import PageHeader from '$lib/components/PageHeader.svelte';
  import DropPlayground from '$lib/components/DropPlayground.svelte';
  import { LUCIDE_ICONS } from '$lib/lucide-icons';
  import type { ArtworkMode, ArtworkQuality, PaletteId } from '@nocturne/watercolour';
  import {
    DETAIL_OPTIONS,
    EASING_OPTIONS,
    OUTPUT_SIZES,
    PLAYGROUND_MODES,
    PLAYGROUND_PALETTES,
    SIM_OVERRIDES,
    PlaygroundState,
    STRIP_EDGE,
    STRIP_FRAMES,
    type EasingName,
    type OutputSize,
    type PlaygroundArtwork,
    type SurfaceMode,
  } from '$lib/playground-state.svelte';

  const pg = new PlaygroundState();
  /** A catalogue artwork on the raw player, or a paint drop on its surface. */
  let subject = $state<'artwork' | 'drop'>('artwork');
  let canvas: HTMLCanvasElement | undefined = $state();

  const titleCase = (s: string) => s.replace(/-/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
  const iconLabel = (id: string) => titleCase(id.slice('lucide:'.length));
  const artworkLabel = $derived(
    pg.artwork.startsWith('lucide:')
      ? iconLabel(pg.artwork)
      : titleCase(pg.availableArtworks.find((a) => a === pg.artwork) ?? pg.artwork),
  );
  const paletteLabel = $derived(PLAYGROUND_PALETTES.find((p) => p.value === pg.palette)?.label ?? '');
  const modeLabel = $derived(PLAYGROUND_MODES.find((m) => m.value === pg.mode)?.label ?? '');
  const detailLabel = $derived(DETAIL_OPTIONS.find((d) => d.value === pg.detailMode)?.label ?? '');
  const easingLabel = $derived(EASING_OPTIONS.find((e) => e.value === pg.easing)?.label ?? '');
  const pct = (v: number) => `${Math.round(v * 100)}%`;
  const ms = (v: number | undefined) => (v === undefined ? '-' : `${v.toFixed(2)} ms`);
  const mb = (bytes: number | undefined) => (bytes === undefined ? '-' : `${(bytes / (1024 * 1024)).toFixed(1)} MB`);

  $effect(() => {
    const el = canvas;
    if (!el) return;
    void pg.attach(el);
    return () => pg.detach();
  });

  let lastKey: string | undefined;
  $effect(() => {
    const key = pg.sceneKey;
    if (lastKey !== undefined && lastKey !== key) pg.recreate();
    lastKey = key;
  });

  function download(blob: Blob, name: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  const fileStem = () => {
    const name = pg.artwork.startsWith('lucide:') ? pg.artwork.slice('lucide:'.length) : pg.artwork;
    return `${name}-${pg.palette}${pg.surface === 'dark' ? '_dark' : ''}-${pg.seed}`;
  };

  async function exportPng() {
    try {
      download(await pg.exportPng(), `${fileStem()}.png`);
      toast('Exported PNG', { description: `${pg.canvasSize.width} by ${pg.canvasSize.height}, straight alpha, sRGB.` });
    } catch (error) {
      toast.error('Export failed', { description: error instanceof Error ? error.message : String(error) });
    }
  }

  async function exportBaked() {
    try {
      const { strip, manifest } = await pg.exportBaked();
      download(strip, `${fileStem()}-strip.png`);
      download(manifest, `${fileStem()}-strip.json`);
      toast('Exported baked strip', { description: `${STRIP_FRAMES} frames of ${STRIP_EDGE} px plus manifest.` });
    } catch (error) {
      toast.error('Export failed', { description: error instanceof Error ? error.message : String(error) });
    }
  }
</script>

<svelte:head>
  <title>Playground - Watercolour showcase</title>
</svelte:head>

<PageHeader
  title="Playground"
  description="The real engine: every artwork at its own aspect with every palette, detail tier, sim resolution and easing, or a paint drop on a surface of any size."
/>

<ToggleGroup.Root
  type="single"
  variant="outline"
  size="sm"
  value={subject}
  onValueChange={(v) => { if (v) subject = v as 'artwork' | 'drop'; }}
  aria-label="Subject"
  class="mb-6 justify-start"
>
  <ToggleGroup.Item value="artwork" aria-label="Artwork">Artwork</ToggleGroup.Item>
  <ToggleGroup.Item value="drop" aria-label="Paint drop">Paint drop</ToggleGroup.Item>
</ToggleGroup.Root>

{#if subject === 'drop'}
  <DropPlayground />
{:else}

{#if pg.webgpuAvailable === false}
  <div role="status" class="flex items-start gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm">
    <TriangleAlert class="mt-0.5 size-4 shrink-0 text-amber-600" />
    <span>
      WebGPU is not available in this browser{pg.capabilityReason ? ` (${pg.capabilityReason})` : ''}. The playground falls back to
      the baked frame strip; live simulation and export need a WebGPU-capable browser.
    </span>
  </div>
{/if}

<div class="grid gap-6 lg:grid-cols-[minmax(0,320px)_minmax(0,1fr)]">
  <Card.Root class="self-start">
    <Card.Header>
      <Card.Title>Scene</Card.Title>
    </Card.Header>
    <Card.Content class="grid gap-5">
      <div class="grid gap-2">
        <Label>Artwork</Label>
        <Select.Root type="single" value={pg.artwork} onValueChange={(v) => (pg.artwork = v as PlaygroundArtwork)}>
          <Select.Trigger class="w-full" aria-label="Artwork">{artworkLabel}</Select.Trigger>
          <Select.Content>
            <Select.Group>
              {#each pg.availableArtworks as id (id)}
                <Select.Item value={id} label={artworkLabel}>
                  {titleCase(id)}
                </Select.Item>
              {/each}
            </Select.Group>
            <Select.Group>
              <Select.Label>Lucide icons</Select.Label>
              {#each LUCIDE_ICONS as l (l.id)}
                <Select.Item value={l.id} label={titleCase(l.name)}>
                  {titleCase(l.name)}
                </Select.Item>
              {/each}
            </Select.Group>
          </Select.Content>
        </Select.Root>
      </div>
      <div class="grid grid-cols-2 gap-3">
        <div class="grid gap-2">
          <Label>Palette</Label>
          <Select.Root type="single" value={pg.palette} onValueChange={(v) => (pg.palette = v as PaletteId)}>
            <Select.Trigger class="w-full" aria-label="Palette">{paletteLabel}</Select.Trigger>
            <Select.Content>
              {#each PLAYGROUND_PALETTES as p (p.value)}
                <Select.Item value={p.value} label={p.label}>{p.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="grid gap-2">
          <Label>Surface</Label>
          <Select.Root type="single" value={pg.surfaceMode} onValueChange={(v) => (pg.surfaceMode = v as SurfaceMode)}>
            <Select.Trigger class="w-full" aria-label="Surface">
              {pg.surfaceMode === 'auto' ? `Auto (${pg.surface})` : pg.surfaceMode === 'dark' ? 'Dark' : 'Light'}
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
          <Label for="pg-seed">Seed</Label>
          <Input
            id="pg-seed"
            type="number"
            value={pg.seed}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => (pg.seed = Math.max(0, Number(e.currentTarget.value) || 0))}
          />
        </div>
        <div class="grid gap-2">
          <Label for="pg-duration">Duration (ms)</Label>
          <Input
            id="pg-duration"
            type="number"
            min="100"
            step="100"
            value={pg.durationMs}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => (pg.durationMs = Math.max(100, Number(e.currentTarget.value) || 3000))}
          />
        </div>
      </div>
      <div class="grid gap-2">
        <div class="flex justify-between text-sm"><Label>Intensity</Label><span class="tabular-nums text-muted-foreground">{pct(pg.intensity)}</span></div>
        <Slider
          type="single"
          value={pg.intensity}
          onValueCommit={(v) => (pg.intensity = v)}
          min={0}
          max={1}
          step={0.05}
          aria-label="Intensity"
        />
      </div>
      <div class="grid grid-cols-2 gap-3">
        <div class="grid gap-2">
          <Label>Detail</Label>
          <Select.Root type="single" value={pg.detailMode} onValueChange={(v) => (pg.detailMode = v as 'auto' | 'small' | 'medium' | 'large' | 'extraLarge')}>
            <Select.Trigger class="w-full" aria-label="Detail">{detailLabel}</Select.Trigger>
            <Select.Content>
              {#each DETAIL_OPTIONS as d (d.value)}
                <Select.Item value={d.value} label={d.label}>{d.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="grid gap-2">
          <Label>Sim grid</Label>
          <Select.Root type="single" value={String(pg.simOverride)} onValueChange={(v) => (pg.simOverride = Number(v))}>
            <Select.Trigger class="w-full" aria-label="Simulation grid">{pg.simOverride === 0 ? 'Auto' : pg.simOverride}</Select.Trigger>
            <Select.Content>
              {#each SIM_OVERRIDES as r (r)}
                <Select.Item value={String(r)} label={r === 0 ? 'Auto' : String(r)}>{r === 0 ? 'Auto' : r}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      </div>
      <div class="grid grid-cols-2 gap-3">
        <div class="grid gap-2">
          <Label>Output size</Label>
          <Select.Root type="single" value={String(pg.outputSize)} onValueChange={(v) => (pg.outputSize = Number(v) as OutputSize)}>
            <Select.Trigger class="w-full" aria-label="Output size">{pg.outputSize} px</Select.Trigger>
            <Select.Content>
              {#each OUTPUT_SIZES as s (s)}
                <Select.Item value={String(s)} label={`${s} px`}>{s} px</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        <div class="grid gap-2">
          <Label>Quality</Label>
          <Select.Root type="single" value={pg.quality} onValueChange={(v) => (pg.quality = v as ArtworkQuality)}>
            <Select.Trigger class="w-full capitalize" aria-label="Quality">{pg.quality}</Select.Trigger>
            <Select.Content>
              {#each ['auto', 'low', 'medium', 'high'] as q (q)}
                <Select.Item value={q} label={q} class="capitalize">{q}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      </div>
      <div class="grid gap-2">
        <Label>Easing</Label>
        <Select.Root type="single" value={pg.easing} onValueChange={(v) => (pg.easing = v as EasingName)}>
          <Select.Trigger class="w-full" aria-label="Easing">{easingLabel}</Select.Trigger>
          <Select.Content>
            {#each EASING_OPTIONS as e (e.value)}
              <Select.Item value={e.value} label={e.label}>{e.label}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="grid gap-2">
        <div class="flex justify-between gap-2 text-sm">
          <Label>Tail</Label>
          <span class="tabular-nums text-muted-foreground">
            {Math.round(pg.durationMs * (1 - pg.tail))} ms brush / {Math.round(pg.durationMs * pg.tail)} ms set
          </span>
        </div>
        <Slider type="single" value={pg.tail} onValueCommit={(v) => (pg.tail = v)} min={0} max={0.95} step={0.05} aria-label="Tail" />
      </div>
      <div class="grid gap-2">
        <Label>Mode</Label>
        <Select.Root type="single" value={pg.mode} onValueChange={(v) => (pg.mode = v as ArtworkMode)}>
          <Select.Trigger class="w-full" aria-label="Mode">{modeLabel}</Select.Trigger>
          <Select.Content>
            {#each PLAYGROUND_MODES as m (m.value)}
              <Select.Item value={m.value} label={m.label}>{m.label}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>

      <Separator />

      <div class="grid gap-2">
        <Label>Replay</Label>
        <div class="flex flex-wrap gap-2">
          {#if pg.playing}
            <Button variant="outline" size="sm" onclick={() => pg.pause()}><Pause /> Pause</Button>
          {:else}
            <Button variant="outline" size="sm" onclick={() => pg.play()}><Play /> Play</Button>
          {/if}
          <Button variant="outline" size="sm" onclick={() => pg.reset()}><RotateCcw /> Reset</Button>
          <Button variant="outline" size="sm" onclick={() => pg.finish()}><SkipForward /> Finish</Button>
        </div>
        <div class="flex items-center gap-3">
          <!-- Snapped to the step: bits-ui reports an off-grid programmatic value back through onValueChange. -->
          <Slider
            type="single"
            value={Math.round(pg.progress * 1000) / 1000}
            onValueChange={(v) => pg.scrub(v)}
            min={0}
            max={1}
            step={0.001}
            aria-label="Scrub"
          />
          <span class="w-12 text-right text-sm tabular-nums text-muted-foreground">{pct(pg.progress)}</span>
        </div>
      </div>

      <Separator />

      <div class="grid gap-2">
        <Label>Export</Label>
        <div class="flex flex-wrap gap-2">
          <Button size="sm" disabled={pg.resolvedMode !== 'live' || pg.exporting} onclick={exportPng}>
            <Download /> PNG
          </Button>
          <Button size="sm" variant="secondary" disabled={pg.resolvedMode !== 'live' || pg.exporting} onclick={exportBaked}>
            <Download /> Baked strip
          </Button>
        </div>
        {#if pg.resolvedMode !== 'live' && pg.resolvedMode !== 'pending'}
          <p class="text-xs text-muted-foreground">Export needs the live engine; the current mode is {pg.resolvedMode}.</p>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>

  <div class="grid gap-6">
    <Card.Root>
      <Card.Header>
        <Card.Title>Canvas</Card.Title>
        <Card.Description>
          Mode {pg.resolvedMode}. {pg.playing ? 'Playing' : pg.finished ? 'Finished' : 'Paused'} at {pct(pg.progress)}.
          {#if pg.fallbackReason}
            Fell back: {pg.fallbackReason}.
          {/if}
          {#if pg.error}
            Error: {pg.error.code}: {pg.error.message}
          {/if}
        </Card.Description>
      </Card.Header>
      <Card.Content class="flex flex-col items-center">
        <div
          data-playground-canvas
          data-mode={pg.resolvedMode}
          data-progress={pg.progress}
          data-playing={pg.playing}
          class="relative w-full rounded-lg outline-1 outline-dashed -outline-offset-1 outline-border"
          style="max-width:{pg.canvasSize.width}px; aspect-ratio:{pg.canvasSize.width} / {pg.canvasSize.height}"
        >
          <canvas
            bind:this={canvas}
            width={pg.canvasSize.width * pg.dpr}
            height={pg.canvasSize.height * pg.dpr}
            class="block h-full w-full"
            aria-hidden="true"
          ></canvas>
        </div>
        <dl class="mt-3 grid w-full grid-cols-2 gap-x-6 gap-y-1 font-mono text-xs sm:grid-cols-4">
          <div>
            <dt class="text-muted-foreground">backing</dt>
            <dd>{pg.canvasSize.width} x {pg.canvasSize.height} CSS px @ {pg.dpr}x</dd>
          </div>
          <div>
            <dt class="text-muted-foreground">detail</dt>
            <dd>{pg.resolvedDetail ?? pg.detailMode}</dd>
          </div>
          <div>
            <dt class="text-muted-foreground">sim grid</dt>
            <dd>{pg.simResolution === undefined ? '-' : pg.simResolution}</dd>
          </div>
          <div>
            <dt class="text-muted-foreground">ticks</dt>
            <dd>{pg.totalTicks === undefined ? '-' : pg.totalTicks}</dd>
          </div>
        </dl>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <Card.Title>Engine stats</Card.Title>
        <Card.Description>Refreshed four times a second, never per frame.</Card.Description>
      </Card.Header>
      <Card.Content>
        <dl data-playground-stats class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 font-mono text-xs">
          <dt class="text-muted-foreground">adapter</dt><dd>{pg.adapter ?? pg.stats.engine?.adapterName ?? '-'}</dd>
          <dt class="text-muted-foreground">instances</dt><dd>{pg.stats.engine ? `${pg.stats.engine.liveInstances} / ${pg.stats.engine.maxLiveInstances}` : '-'}</dd>
          <dt class="text-muted-foreground">checkpoints</dt><dd>{mb(pg.stats.engine?.checkpointBytes)}</dd>
          <dt class="text-muted-foreground">gpu init</dt><dd>{ms(pg.stats.engine?.initMs)}</dd>
          <dt class="text-muted-foreground">last step</dt><dd>{ms(pg.stats.engine?.lastStepMs)}</dd>
          <dt class="text-muted-foreground">last render</dt><dd>{ms(pg.stats.engine?.lastRenderMs)}</dd>
          <dt class="text-muted-foreground">frame avg / p95</dt><dd>{pg.stats.frames.frames ? `${ms(pg.stats.frames.averageMs)} / ${ms(pg.stats.frames.p95Ms)} (${pg.stats.frames.frames})` : '-'}</dd>
          <dt class="text-muted-foreground">first paint</dt><dd>{ms(pg.stats.firstPaintMs)}</dd>
        </dl>
      </Card.Content>
    </Card.Root>
  </div>
</div>
{/if}
