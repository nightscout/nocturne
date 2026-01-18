<script lang="ts">
    import { page } from "$app/state";
    import { Button } from "@nocturne/app/ui/button";
    import { Menu, X } from "@lucide/svelte";
    import {
        DEMO_ENABLED,
    } from "$lib/config";
    import LanguageSelector from "./LanguageSelector.svelte";

    let mobileMenuOpen = $state(false);

    const baseNavLinks = [
        { href: "/features", label: "Features" },
        { href: "/docs", label: "Docs" },
        { href: "/faq", label: "FAQ" },
    ];

    // Add Demo link when demo is enabled
    const navLinks = DEMO_ENABLED
        ? [...baseNavLinks.slice(0, 2), { href: "/demo", label: "Demo" }, ...baseNavLinks.slice(2)]
        : baseNavLinks;

    const isActive = (href: string) => {
        return page.url.pathname === href || page.url.pathname.startsWith(href + "/");
    };
</script>

<header class="sticky top-0 z-50 border-b border-border/40 bg-background/80 backdrop-blur-md">
    <div class="container mx-auto px-4">
        <div class="flex h-16 items-center justify-between">
            <!-- Logo -->
            <a
                href="/"
                class="flex items-center gap-2.5 hover:opacity-80 transition-opacity"
            >
                <div
                    class="w-9 h-9 rounded-lg bg-primary/20 flex items-center justify-center"
                >
                    <span class="text-primary font-bold text-lg">N</span>
                </div>
                <span class="text-xl font-semibold">Nocturne</span>
            </a>

            <!-- Desktop Navigation -->
            <nav class="hidden md:flex items-center gap-1">
                {#each navLinks as link}
                    <a
                        href={link.href}
                        class="px-4 py-2 text-sm font-medium rounded-md transition-colors {isActive(link.href)
                            ? 'text-foreground bg-muted'
                            : 'text-muted-foreground hover:text-foreground hover:bg-muted/50'}"
                    >
                        {link.label}
                    </a>
                {/each}
            </nav>

            <!-- Desktop CTA -->
            <div class="hidden md:flex items-center gap-3">
                <LanguageSelector compact />
                <Button href="/setup" variant="default" size="sm">
                    Get Started
                </Button>
            </div>

            <!-- Mobile Menu Button -->
            <button
                type="button"
                class="md:hidden p-2 rounded-md hover:bg-muted transition-colors"
                onclick={() => (mobileMenuOpen = !mobileMenuOpen)}
                aria-label="Toggle menu"
            >
                {#if mobileMenuOpen}
                    <X class="w-5 h-5" />
                {:else}
                    <Menu class="w-5 h-5" />
                {/if}
            </button>
        </div>

        <!-- Mobile Navigation -->
        {#if mobileMenuOpen}
            <nav class="md:hidden py-4 border-t border-border/40">
                <div class="flex flex-col gap-1">
                    {#each navLinks as link}
                        <a
                            href={link.href}
                            class="px-4 py-3 text-sm font-medium rounded-md transition-colors {isActive(link.href)
                                ? 'text-foreground bg-muted'
                                : 'text-muted-foreground hover:text-foreground hover:bg-muted/50'}"
                            onclick={() => (mobileMenuOpen = false)}
                        >
                            {link.label}
                        </a>
                    {/each}
                    <div class="pt-3 mt-2 border-t border-border/40 space-y-3">
                        <div class="px-4">
                            <LanguageSelector />
                        </div>
                        <Button href="/setup" variant="default" class="w-full">
                            Get Started
                        </Button>
                    </div>
                </div>
            </nav>
        {/if}
    </div>
</header>
