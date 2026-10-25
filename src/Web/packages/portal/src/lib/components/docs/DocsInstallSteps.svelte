<script lang="ts">
    import { tick } from "svelte";
    import { SvelteSet } from "svelte/reactivity";
    import { afterNavigate } from "$app/navigation";
    import { track } from "$lib/analytics";

    // A step counts as reached once its marker is in the upper 60% of the viewport, where the
    // reader is looking, rather than as soon as its edge appears at the bottom.
    const ROOT_MARGIN = "0px 0px -40% 0px";

    let observer: IntersectionObserver | undefined;

    // Each docs page has its own markers, and the layout survives navigation between them, so
    // the observer is rebuilt for every pageview.
    afterNavigate(async () => {
        observer?.disconnect();
        await tick();

        const markers = document.querySelectorAll("[data-install-step]");
        if (markers.length === 0) return;

        const reached = new SvelteSet<string>();
        observer = new IntersectionObserver(
            (entries) => {
                for (const entry of entries) {
                    if (!entry.isIntersecting) continue;
                    const step = entry.target.getAttribute("data-install-step");
                    if (!step || reached.has(step)) continue;
                    reached.add(step);
                    observer?.unobserve(entry.target);
                    track("Install Step", { step });
                }
            },
            { rootMargin: ROOT_MARGIN },
        );
        for (const marker of markers) observer.observe(marker);
    });

    $effect(() => () => observer?.disconnect());
</script>
