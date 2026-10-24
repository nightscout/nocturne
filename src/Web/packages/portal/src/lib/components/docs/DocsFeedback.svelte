<script lang="ts">
    import ThumbsUp from "@lucide/svelte/icons/thumbs-up";
    import ThumbsDown from "@lucide/svelte/icons/thumbs-down";
    import ArrowUpRight from "@lucide/svelte/icons/arrow-up-right";
    import { page } from "$app/state";
    import { Button } from "@nocturne/ui/ui/button";
    import { campaignUrl, track } from "$lib/analytics";
    import { LINKS } from "$lib/data/links";

    let { class: className = "" }: { class?: string } = $props();

    const donateUrl = campaignUrl(LINKS.donate, "docs-feedback");

    // Only rendered after a click, so `page.url` is the browser's address, not the prerender's.
    const issueUrl = $derived.by(() => {
        const address = `${page.url.origin}${page.url.pathname}`;
        const query = new URLSearchParams({
            template: "docs-feedback.yml",
            title: `Docs: ${page.url.pathname}`,
            page: address,
        });
        return `${LINKS.docsIssue}?${query}`;
    });

    let answer = $state<"yes" | "no" | undefined>();

    function answerWith(helpful: "yes" | "no") {
        answer = helpful;
        track("Docs Feedback", { helpful });
    }
</script>

<aside class={className} aria-label="Page feedback">
    {#if answer === undefined}
        <div class="flex flex-wrap items-center gap-3">
            <p class="m-0 text-sm text-muted-foreground">Was this helpful?</p>
            <div class="flex gap-2">
                <Button variant="outline" size="sm" onclick={() => answerWith("yes")}>
                    <ThumbsUp aria-hidden="true" />
                    Yes
                </Button>
                <Button variant="outline" size="sm" onclick={() => answerWith("no")}>
                    <ThumbsDown aria-hidden="true" />
                    No
                </Button>
            </div>
        </div>
    {/if}

    <div role="status">
        {#if answer === "yes"}
            <p class="m-0 text-sm text-muted-foreground">
                Thank you. Consider
                <a
                    href={donateUrl}
                    target="_blank"
                    rel="external noopener noreferrer"
                    onclick={() =>
                        track("Donate Click", { destination: "foundation", placement: "docs-feedback" })}
                    class="inline-flex items-center gap-0.5 font-semibold text-brand hover:underline"
                    >donating<ArrowUpRight class="size-3.5" aria-hidden="true" /></a
                >
                to support our work.
            </p>
        {:else if answer === "no"}
            <p class="m-0 text-sm text-muted-foreground">
                Thanks for telling us.
                <a
                    href={issueUrl}
                    target="_blank"
                    rel="external noopener noreferrer"
                    onclick={() => track("Outbound Click", { destination: "docs-issue" })}
                    class="inline-flex items-center gap-0.5 font-semibold text-brand hover:underline"
                    >Tell us what was missing<ArrowUpRight class="size-3.5" aria-hidden="true" /></a
                >
            </p>
        {/if}
    </div>
</aside>
