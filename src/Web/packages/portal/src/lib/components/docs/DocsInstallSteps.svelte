<script lang="ts">
    import { tick } from "svelte";
    import { SvelteSet } from "svelte/reactivity";
    import { afterNavigate } from "$app/navigation";
    import { track } from "#lib/analytics.js";

    // A step counts as reached once its marker is in the upper 60% of the viewport, where the
    // reader is looking, rather than as soon as its edge appears at the bottom.
    const ROOT_MARGIN = "0px 0px -40% 0px";

    let observer: IntersectionObserver | undefined;
    let added: MutationObserver | undefined;

    // Each docs page has its own markers, and the layout survives navigation between them, so
    // the observers are rebuilt for every pageview.
    afterNavigate(async () => {
        observer?.disconnect();
        added?.disconnect();
        await tick();

        const markers = document.querySelectorAll("[data-install-step]");
        if (markers.length === 0) return;

        const reached = new SvelteSet<string>();
        const intersections = new IntersectionObserver(
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
        observer = intersections;
        for (const marker of markers) intersections.observe(marker);

        // A guide shows only the route the reader selected, so a change of route adds markers.
        added = new MutationObserver((mutations) => {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) {
                    if (!(node instanceof Element)) continue;
                    if (node.matches("[data-install-step]")) intersections.observe(node);
                    for (const marker of node.querySelectorAll("[data-install-step]")) {
                        intersections.observe(marker);
                    }
                }
            }
        });
        added.observe(markers[0].closest("article") ?? document.body, { childList: true, subtree: true });
    });

    $effect(() => () => {
        observer?.disconnect();
        added?.disconnect();
    });
</script>
