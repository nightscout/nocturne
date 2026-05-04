import { crossfade } from "svelte/transition";
import { quintOut } from "svelte/easing";

// Shared crossfade for the rule builder. A single instance must be reused
// across every `{#each}` so a row leaving one group and re-keying into
// another tweens its bounding box instead of fade-in/fade-out in place.
export const [sendRow, receiveRow] = crossfade({
	duration: 220,
	easing: quintOut,
	fallback(node) {
		return {
			duration: 160,
			easing: quintOut,
			css: (t: number) => `opacity: ${t}; transform: scale(${0.96 + 0.04 * t});`,
		};
	},
});
