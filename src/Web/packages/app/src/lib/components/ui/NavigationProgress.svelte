<script lang="ts">
	import { navigating } from '$app/state';

	let active = $derived(!!navigating.to);
	let fading = $state(false);
	let visible = $derived(active || fading);

	$effect(() => {
		if (!active && !fading) return;
		if (active) return;
		// Navigation just completed -- fade out then hide
		const timeout = setTimeout(() => (fading = false), 300);
		return () => clearTimeout(timeout);
	});

	$effect(() => {
		if (active) fading = true;
	});
</script>

{#if visible}
	<div
		class="fixed inset-x-0 top-0 z-50 h-0.75"
		class:completing={!active}
		class:navigating={active}
	>
		<div class="bar h-full bg-primary shadow-[0_0_8px_hsl(var(--primary)/0.4)]"></div>
	</div>
{/if}

<style>
	.navigating .bar {
		animation: grow 2s ease-out forwards, pulse 1.5s ease-in-out 2s infinite;
	}
	.completing .bar {
		width: 100%;
		transition: width 150ms ease-out;
	}
	.completing {
		animation: fade-out 300ms ease-out forwards;
	}
	@keyframes grow {
		from { width: 0%; }
		to { width: 80%; }
	}
	@keyframes pulse {
		0%, 100% { opacity: 1; }
		50% { opacity: 0.6; }
	}
	@keyframes fade-out {
		from { opacity: 1; }
		to { opacity: 0; }
	}
</style>
