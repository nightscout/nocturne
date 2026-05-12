<script lang="ts">
    import * as Accordion from "@nocturne/ui/ui/accordion";
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight, HelpCircle, Download, RefreshCw, Code } from "@lucide/svelte";

    const faqCategories = [
        {
            title: "General",
            icon: HelpCircle,
            color: "bg-blue-500/15 text-blue-500",
            questions: [
                {
                    question: "What is Nocturne?",
                    answer: "Nocturne is a modern, open-source rewrite of the Nightscout diabetes management platform. Built on .NET 10 with a SvelteKit frontend, it provides the same API compatibility as Nightscout while offering improved performance, modern architecture, and easier deployment.",
                },
                {
                    question: "How does Nocturne compare to Nightscout?",
                    answer: "Nocturne is API-compatible with Nightscout (v1, v2, and v3 APIs), so your existing apps and devices work without changes. The main differences are under the hood: Nocturne uses PostgreSQL instead of MongoDB, is built on .NET for better performance, and includes modern tooling like Aspire for orchestration and observability.",
                },
                {
                    question: "Is Nocturne free?",
                    answer: "Nocturne is free and open source under the AGPL-3.0 license. You can self-host it, modify it, and contribute to it at no cost. A commercial license is also available for organisations that need to integrate Nocturne without the AGPL's source-disclosure requirements.",
                },
                {
                    question: "Who is Nocturne for?",
                    answer: "Nocturne is for anyone who uses Nightscout or wants to self-host their diabetes data. Whether you're using a DIY closed loop system, want to share your glucose data with caregivers, or just want to track your data independently, Nocturne can help.",
                },
                {
                    question: "How does the licensing work?",
                    answer: "Nocturne is dual-licensed. For individuals and community self-hosters, it is available under the AGPL-3.0 — free to use, modify, and self-host, with the requirement that any modifications you distribute are also open source. For organisations that need to integrate Nocturne into proprietary products or services without those source-disclosure obligations — such as clinics, device manufacturers, or diabetes management platforms — a commercial license is available. This model lets us build a sustainable revenue stream with partnering organisations while fully protecting the rights of individual users and the broader diabetes community.",
                },
            ],
        },
        {
            title: "Installation",
            icon: Download,
            color: "bg-green-500/15 text-green-500",
            questions: [
                {
                    question: "What are the system requirements?",
                    answer: "Nocturne requires Docker to run. Any system that can run Docker (Linux, Windows, macOS) can host Nocturne. For a single user, a small VPS with 1GB RAM is sufficient. For families or multiple users, we recommend 2GB+ RAM.",
                },
                {
                    question: "Can I run Nocturne on a Raspberry Pi?",
                    answer: "Yes! Nocturne runs well on Raspberry Pi 4 and newer models. The ARM64 Docker images are available for these platforms.",
                },
                {
                    question: "Do I need technical knowledge to set up Nocturne?",
                    answer: "Basic familiarity with Docker and command line is helpful, but our configuration wizard generates all the files you need. Most users can get up and running by following our getting started guide.",
                },
                {
                    question: "Can I use an existing PostgreSQL database?",
                    answer: "Yes! While Nocturne includes a PostgreSQL container by default, you can configure it to use any external PostgreSQL database. This is useful if you already have managed database hosting.",
                },
            ],
        },
        {
            title: "Migration",
            icon: RefreshCw,
            color: "bg-orange-500/15 text-orange-500",
            questions: [
                {
                    question: "Can I migrate my existing Nightscout data?",
                    answer: "Yes! Nocturne includes built-in migration tools to import your complete history from Nightscout. Your entries, treatments, and profile data can all be imported.",
                },
                {
                    question: "Will my existing apps still work?",
                    answer: "Yes! Nocturne is fully API-compatible with Nightscout. xDrip+, Loop, AndroidAPS, and other apps that work with Nightscout will work with Nocturne without any configuration changes (just point them to your new Nocturne URL).",
                },
                {
                    question: "Can I run Nocturne alongside Nightscout?",
                    answer: "Yes! The Compatibility Proxy mode lets you run Nocturne alongside your existing Nightscout instance. This is great for testing Nocturne before fully migrating.",
                },
                {
                    question: "What happens to my Nightscout during migration?",
                    answer: "Migration is read-only - it copies data from Nightscout without modifying your original instance. You can keep your Nightscout running during and after migration until you're confident in your Nocturne setup.",
                },
            ],
        },
        {
            title: "Technical",
            icon: Code,
            color: "bg-purple-500/15 text-purple-500",
            questions: [
                {
                    question: "What technology stack does Nocturne use?",
                    answer: "Nocturne is built on .NET 10 for the backend API, PostgreSQL for data storage, and SvelteKit 2 with Svelte 5 for the frontend. It uses .NET Aspire for service orchestration and observability.",
                },
                {
                    question: "How do data connectors work?",
                    answer: "Connectors are background services that fetch data from external sources like Dexcom Share or LibreView. Each connector authenticates with its data source and periodically syncs glucose readings to your Nocturne instance.",
                },
                {
                    question: "Is there an API?",
                    answer: "Yes! Nocturne implements the full Nightscout API (v1, v2, and v3) plus additional endpoints. Interactive API documentation is available through Scalar when enabled.",
                },
                {
                    question: "Can I contribute to Nocturne?",
                    answer: "Absolutely! Nocturne is open source and welcomes contributions. Check out our GitHub repository for contribution guidelines, or join the community to discuss features and improvements.",
                },
            ],
        },
    ];
</script>

<div class="max-w-[900px] mx-auto px-6">
    <!-- Page heading -->
    <div class="pt-20 pb-[60px] border-b border-border">
        <div class="font-mono text-[11px] tracking-[0.14em] uppercase text-muted-foreground mb-4">FAQ</div>
        <h1 class="text-[clamp(2rem,4vw,3.2rem)] font-bold leading-[1.15] tracking-[-0.025em] text-foreground m-0 mb-4">
            Common questions.<br />
            <em class="text-glucose-in-range">Straight answers.</em>
        </h1>
        <p class="text-base leading-[1.65] text-muted-foreground max-w-[520px] m-0">
            Answers to frequent questions about Nocturne, installation, migration,
            and the technology stack.
        </p>
    </div>

    <!-- FAQ Categories -->
    <div class="flex flex-col">
        {#each faqCategories as category, ci}
            <section class="py-16 border-t border-border">
                <div class="mb-8">
                    <div class="font-brand text-[12px] font-bold tracking-[0.14em] uppercase text-muted-foreground">0{ci + 1} &mdash; {category.title}</div>
                </div>

                <Accordion.Root type="multiple" class="space-y-3">
                    {#each category.questions as faq, index}
                        <Accordion.Item
                            value="{category.title}-{index}"
                            class="rounded-lg border border-border/60 bg-card/50 px-6 overflow-hidden"
                        >
                            <Accordion.Trigger
                                class="py-4 text-left font-medium hover:no-underline w-full"
                            >
                                {faq.question}
                            </Accordion.Trigger>
                            <Accordion.Content class="pb-4">
                                <p class="text-muted-foreground">{faq.answer}</p>
                            </Accordion.Content>
                        </Accordion.Item>
                    {/each}
                </Accordion.Root>
            </section>
        {/each}
    </div>

    <!-- Still Have Questions -->
    <section class="border-t border-border py-20">
        <div class="font-mono text-[11px] tracking-[0.14em] uppercase text-muted-foreground">Still have questions?</div>
        <h2 class="text-[clamp(1.4rem,2.5vw,2rem)] font-bold tracking-[-0.02em] text-foreground mt-3">Check the docs or ask the community.</h2>
        <div class="flex flex-col sm:flex-row gap-4 mt-6">
            <Button href="/docs" size="lg" class="gap-2">
                Browse documentation
                <ArrowRight class="w-4 h-4" />
            </Button>
            <Button
                href="https://github.com/nightscout/nocturne"
                variant="outline"
                size="lg"
                target="_blank"
                rel="noopener noreferrer"
            >
                Visit GitHub
            </Button>
        </div>
    </section>
</div>
