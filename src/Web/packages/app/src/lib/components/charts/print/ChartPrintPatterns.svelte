<!--
  Global SVG <pattern> defs and their `.chart-patterns-on` rules, both generated
  from CHART_TEXTURES. Mounted once (authenticated layout).
  The host SVG is zero-size but not `display:none` — some browsers won't resolve
  a `url(#id)` paint server defined inside a `display:none` subtree while printing.
-->
<script lang="ts">
	import { chartAlwaysShowPatterns } from "$lib/stores/appearance-store.svelte";
	import { patternTiles, textureStylesheet } from "./chart-print-patterns";
	import { PrintMode } from "./print-mode.svelte";

	const tiles = patternTiles();
	const stylesheet = `<style data-chart-textures>${textureStylesheet()}</style>`;

	// Patterns activate while printing or when the accessibility setting forces
	// them on screen; both paths toggle `.chart-patterns-on` on the document root.
	const print = new PrintMode();
	$effect(() => {
		document.documentElement.classList.toggle(
			"chart-patterns-on",
			chartAlwaysShowPatterns.current || print.active
		);
		return () => document.documentElement.classList.remove("chart-patterns-on");
	});
</script>

<svelte:head>
	<!-- eslint-disable-next-line svelte/no-at-html-tags -- static, generated from constants -->
	{@html stylesheet}
</svelte:head>

<svg aria-hidden="true" focusable="false" class="pointer-events-none absolute -z-50 h-0 w-0 overflow-hidden">
	<defs>
		{#each tiles as tile (tile.id)}
			<pattern
				id={tile.id}
				width={tile.size}
				height={tile.size}
				patternUnits="userSpaceOnUse"
				patternTransform={tile.rotate ? `rotate(${tile.rotate})` : undefined}
			>
				{#if tile.color}
					<rect width={tile.size} height={tile.size} fill={tile.color} />
				{/if}
				{#if tile.shape === "line"}
					<line x1={tile.size / 2} y1="0" x2={tile.size / 2} y2={tile.size} stroke={tile.ink} stroke-width="1.4" />
				{:else if tile.shape === "grid"}
					<path d="M 0 0 L 0 {tile.size} M 0 0 L {tile.size} 0" stroke={tile.ink} stroke-width="1.1" fill="none" />
				{:else if tile.shape === "dot"}
					<circle cx={tile.size / 2} cy={tile.size / 2} r="1.1" fill={tile.ink} />
				{/if}
			</pattern>
		{/each}
	</defs>
</svg>
