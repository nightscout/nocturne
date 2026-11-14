<script lang="ts">
    import Globe from "@lucide/svelte/icons/globe";
    import Check from "@lucide/svelte/icons/check";
    import { Input } from "@nocturne/ui/ui/input";
    import { Label } from "@nocturne/ui/ui/label";
    import { getInstallGuide } from "./guide.svelte.js";

    interface Step {
        /** The id of the step's heading. */
        id: string;
        title: string;
    }

    interface Props {
        steps: Step[];
        placeholder?: string;
    }

    let { steps, placeholder = "example.com" }: Props = $props();

    const guide = getInstallGuide();

    let bar = $state<HTMLElement | null>(null);
    // -1 is above the first step.
    let current = $state(-1);

    // The step whose heading last passed under the bar. A route choice adds and removes
    // headings, so each pass looks them up again.
    function locate() {
        const line = (bar?.getBoundingClientRect().bottom ?? 0) + 64;
        let found = -1;
        steps.forEach((step, index) => {
            const heading = document.getElementById(step.id);
            if (heading && heading.getBoundingClientRect().top <= line) found = index;
        });
        current = found;
    }

    $effect(() => {
        let frame = 0;
        const onScroll = () => {
            cancelAnimationFrame(frame);
            frame = requestAnimationFrame(locate);
        };
        locate();
        window.addEventListener("scroll", onScroll, { passive: true });
        window.addEventListener("resize", onScroll);
        return () => {
            cancelAnimationFrame(frame);
            window.removeEventListener("scroll", onScroll);
            window.removeEventListener("resize", onScroll);
        };
    });
</script>

{#if guide}
    <div
        bind:this={bar}
        data-install-bar
        class="not-prose clear-both sticky top-[calc(4rem+1px)] z-30 -mx-4 mb-8 border-y border-border/60 bg-background/90 px-4 py-2.5 backdrop-blur-md sm:mx-0 sm:rounded-lg sm:border"
    >
        <div class="flex flex-wrap items-center gap-x-6 gap-y-2">
            <div class="flex min-w-0 flex-1 basis-64 items-center gap-2">
                <Label for="install-domain" size="sm" variant="muted" class="shrink-0">
                    <Globe class="size-3.5" aria-hidden="true" />
                    Your domain
                </Label>
                <Input
                    id="install-domain"
                    bind:ref={guide.domainField}
                    value={guide.domainInput}
                    oninput={(event) => guide.setDomain(event.currentTarget.value)}
                    size="sm"
                    {placeholder}
                    autocomplete="off"
                    autocapitalize="off"
                    spellcheck={false}
                    valid={guide.domainValid}
                    aria-invalid={guide.domain !== "" && !guide.domainValid}
                    aria-describedby="install-domain-hint"
                />
            </div>

            <nav aria-label="Steps in this guide" class="flex min-w-0 items-center gap-3">
                <ol class="m-0 flex list-none items-center gap-1.5 p-0">
                    {#each steps as step, index (step.id)}
                        <li>
                            <a
                                href="#{step.id}"
                                title={step.title}
                                aria-label="Step {index + 1}: {step.title}"
                                aria-current={index === current ? "step" : undefined}
                                class="flex size-6 items-center justify-center rounded-full border text-xs font-medium no-underline transition-colors
                                    {index === current
                                    ? 'border-primary bg-primary text-primary-foreground'
                                    : index < current
                                      ? 'border-primary/40 bg-primary/10 text-foreground hover:bg-primary/20'
                                      : 'border-border text-muted-foreground hover:bg-accent hover:text-foreground'}"
                            >
                                {#if index < current}
                                    <Check class="size-3" aria-hidden="true" />
                                {:else}
                                    {index + 1}
                                {/if}
                            </a>
                        </li>
                    {/each}
                </ol>
                <p class="m-0 truncate text-sm text-muted-foreground">
                    {#if current >= 0}
                        <span class="text-foreground">{steps[current].title}</span>
                    {:else}
                        {steps.length} steps
                    {/if}
                </p>
            </nav>
        </div>

        <p
            id="install-domain-hint"
            class="m-0 mt-1.5 text-xs {guide.domain !== '' && !guide.domainValid
                ? 'text-destructive'
                : 'text-muted-foreground'}"
            class:sr-only={guide.domainValid}
        >
            {#if guide.domain !== "" && !guide.domainValid}
                Enter only the domain name, for example example.com, without https:// or a path.
            {:else if guide.domainValid}
                This page shows your domain in its addresses and commands.
            {:else}
                Enter your domain. This page then shows your addresses and commands.
            {/if}
        </p>
    </div>
{/if}
