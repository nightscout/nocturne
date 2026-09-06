<!--
  Global SVG <pattern> defs for monochrome charts, referenced document-wide by
  the `.chart-patterns-on` rules in app.css. Mounted once (authenticated layout).
  The host SVG is zero-size but not `display:none` — some browsers won't resolve
  a `url(#id)` paint server defined inside a `display:none` subtree while printing.
-->
<script lang="ts">
	import { browser } from "$app/environment";
	import { chartAlwaysShowPatterns } from "$lib/stores/appearance-store.svelte";

	const GLUCOSE_INK = "rgba(0, 0, 0, 0.6)";
	const CAT_INK = "rgba(0, 0, 0, 0.72)";

	// Patterns activate while printing or when the accessibility setting forces
	// them on screen; both paths toggle `.chart-patterns-on` on the document root.
	$effect(() => {
		const always = chartAlwaysShowPatterns.current;
		if (!browser) return;

		const root = document.documentElement;
		const printMql = window.matchMedia("print");
		const sync = () => root.classList.toggle("chart-patterns-on", always || printMql.matches);
		const forceOn = () => root.classList.add("chart-patterns-on");

		sync();
		printMql.addEventListener("change", sync);
		window.addEventListener("beforeprint", forceOn);
		window.addEventListener("afterprint", sync);

		return () => {
			printMql.removeEventListener("change", sync);
			window.removeEventListener("beforeprint", forceOn);
			window.removeEventListener("afterprint", sync);
			root.classList.toggle("chart-patterns-on", always);
		};
	});
</script>

<svg aria-hidden="true" focusable="false" class="pointer-events-none absolute -z-50 h-0 w-0 overflow-hidden">
	<defs>
		<!-- Glucose ranges: range colour background + monochrome texture. -->

		<!-- very-low: dense back-hatch "\" -->
		<pattern id="print-pat-very-low" width="4" height="4" patternUnits="userSpaceOnUse" patternTransform="rotate(-45)">
			<rect width="4" height="4" fill="var(--glucose-very-low)" />
			<line x1="0" y1="0" x2="0" y2="4" stroke={GLUCOSE_INK} stroke-width="1.4" />
		</pattern>

		<!-- low: open back-hatch "\" -->
		<pattern id="print-pat-low" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(-45)">
			<rect width="8" height="8" fill="var(--glucose-low)" />
			<line x1="0" y1="0" x2="0" y2="8" stroke={GLUCOSE_INK} stroke-width="1.4" />
		</pattern>

		<!-- in-range: solid baseline, no texture -->
		<pattern id="print-pat-in-range" width="4" height="4" patternUnits="userSpaceOnUse">
			<rect width="4" height="4" fill="var(--glucose-in-range)" />
		</pattern>

		<!-- tight-range: dots -->
		<pattern id="print-pat-tight-range" width="6" height="6" patternUnits="userSpaceOnUse">
			<rect width="6" height="6" fill="var(--glucose-tight-range)" />
			<circle cx="3" cy="3" r="1" fill={GLUCOSE_INK} />
		</pattern>

		<!-- high: open forward-hatch "/" -->
		<pattern id="print-pat-high" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
			<rect width="8" height="8" fill="var(--glucose-high)" />
			<line x1="0" y1="0" x2="0" y2="8" stroke={GLUCOSE_INK} stroke-width="1.4" />
		</pattern>

		<!-- very-high: dense forward-hatch "/" -->
		<pattern id="print-pat-very-high" width="4" height="4" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
			<rect width="4" height="4" fill="var(--glucose-very-high)" />
			<line x1="0" y1="0" x2="0" y2="4" stroke={GLUCOSE_INK} stroke-width="1.4" />
		</pattern>

		<!-- Categorical textures: transparent background. -->

		<!-- cat-1: forward-hatch "/" -->
		<pattern id="print-pat-cat-1" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
			<line x1="0" y1="0" x2="0" y2="6" stroke={CAT_INK} stroke-width="1.2" />
		</pattern>

		<!-- cat-2: back-hatch "\" -->
		<pattern id="print-pat-cat-2" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(-45)">
			<line x1="0" y1="0" x2="0" y2="6" stroke={CAT_INK} stroke-width="1.2" />
		</pattern>

		<!-- cat-3: grid / cross-hatch -->
		<pattern id="print-pat-cat-3" width="6" height="6" patternUnits="userSpaceOnUse">
			<path d="M 0 0 L 0 6 M 0 0 L 6 0" stroke={CAT_INK} stroke-width="1.1" fill="none" />
		</pattern>

		<!-- cat-4: dots -->
		<pattern id="print-pat-cat-4" width="6" height="6" patternUnits="userSpaceOnUse">
			<circle cx="3" cy="3" r="1.1" fill={CAT_INK} />
		</pattern>

		<!-- cat-5: horizontal lines -->
		<pattern id="print-pat-cat-5" width="6" height="6" patternUnits="userSpaceOnUse">
			<line x1="0" y1="0" x2="6" y2="0" stroke={CAT_INK} stroke-width="1.2" />
		</pattern>

		<!-- cat-6: vertical lines -->
		<pattern id="print-pat-cat-6" width="6" height="6" patternUnits="userSpaceOnUse">
			<line x1="0" y1="0" x2="0" y2="6" stroke={CAT_INK} stroke-width="1.2" />
		</pattern>
	</defs>
</svg>
