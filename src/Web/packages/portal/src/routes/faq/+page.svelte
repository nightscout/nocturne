<script lang="ts">
    import * as Accordion from "@nocturne/app/ui/accordion";
    import { Button } from "@nocturne/app/ui/button";
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
                    answer: "Yes! Nocturne is completely free and open source under the MIT license. You can use it, modify it, and contribute to it without any cost.",
                },
                {
                    question: "Who is Nocturne for?",
                    answer: "Nocturne is for anyone who uses Nightscout or wants to self-host their diabetes data. Whether you're using a DIY closed loop system, want to share your glucose data with caregivers, or just want to track your data independently, Nocturne can help.",
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

<div class="container mx-auto px-4 py-12">
    <!-- Hero -->
    <div class="text-center mb-16">
        <h1 class="text-4xl md:text-5xl font-bold tracking-tight mb-4">
            Frequently Asked Questions
        </h1>
        <p class="text-lg text-muted-foreground max-w-2xl mx-auto">
            Find answers to common questions about Nocturne, installation, migration,
            and more.
        </p>
    </div>

    <!-- FAQ Categories -->
    <div class="max-w-3xl mx-auto space-y-12">
        {#each faqCategories as category}
            <section>
                <h2 class="text-xl font-bold mb-6 flex items-center gap-3">
                    <div
                        class="w-10 h-10 rounded-lg {category.color} flex items-center justify-center"
                    >
                        <category.icon class="w-5 h-5" />
                    </div>
                    {category.title}
                </h2>

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
    <section class="mt-20 text-center">
        <div
            class="max-w-xl mx-auto rounded-2xl border border-primary/20 bg-card/80 p-8"
        >
            <h2 class="text-2xl font-bold mb-4">Still Have Questions?</h2>
            <p class="text-muted-foreground mb-6">
                Check out the documentation for detailed guides, or visit GitHub
                to ask the community.
            </p>
            <div class="flex flex-col sm:flex-row gap-4 justify-center">
                <Button href="/docs" size="lg" class="gap-2">
                    Browse Documentation
                    <ArrowRight class="w-4 h-4" />
                </Button>
                <Button
                    href="https://github.com/your-org/nocturne"
                    variant="outline"
                    size="lg"
                    target="_blank"
                    rel="noopener noreferrer"
                >
                    Visit GitHub
                </Button>
            </div>
        </div>
    </section>
</div>
