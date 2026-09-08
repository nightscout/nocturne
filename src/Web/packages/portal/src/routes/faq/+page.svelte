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
                    answer: "Nocturne is an open-source, self-hosted diabetes dashboard from the Nightscout Foundation. It speaks the Nightscout API, so the apps and devices that work with Nightscout work with it, and adds built-in connectors, rule-based alarms, multitenancy, and a set of clinical reports.",
                },
                {
                    question: "How does Nocturne compare to Nightscout?",
                    answer: "Nocturne is API-compatible with Nightscout (v1, v2, and v3), so your existing apps and devices work once you point them at the new URL. The differences are in what surrounds the data: one install serves many people, connectors pull from Dexcom, Libre, CareLink and others without an uploader app, alarms are rules rather than thresholds, and sign-in uses passkeys instead of a shared secret. Under the hood it stores data in PostgreSQL rather than MongoDB.",
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
                    answer: "Nocturne is dual-licensed. For individuals and community self-hosters, it is available under the AGPL-3.0: free to use, modify, and self-host, with the requirement that any modifications you distribute are also open source. For organisations that need to integrate Nocturne into proprietary products or services without those source-disclosure obligations (clinics, device manufacturers, or diabetes management platforms, for example) a commercial license is available. This model lets us build a sustainable revenue stream with partnering organisations while fully protecting the rights of individual users and the broader diabetes community.",
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
                    answer: "Nocturne runs in Docker, so any system that runs Docker (Linux, Windows, macOS) can host it. For a single user, 2 GB of RAM and one CPU core will run it, though 4 GB is more comfortable because PostgreSQL uses the spare memory as disk cache. Allow 10 GB of storage, most of which is the container images. For families or several sites, 4 GB or more is recommended. You also need a domain name pointed at the server.",
                },
                {
                    question: "Can I run Nocturne on a Raspberry Pi?",
                    answer: "Yes. Nocturne runs on Raspberry Pi 4 and newer with 64-bit Raspberry Pi OS; the Docker images are published for ARM64 as well as x86_64.",
                },
                {
                    question: "Do I need technical knowledge to set up Nocturne?",
                    answer: "Basic familiarity with Docker and the command line is helpful. Each release ships a ready-made compose file and an environment template; you fill in a domain name, an instance key, and four database passwords. Most people get running by following the installation guide.",
                },
                {
                    question: "Can I use an existing PostgreSQL database?",
                    answer: "Yes. The bundle includes a PostgreSQL container, but you can point Nocturne at any PostgreSQL 17 database, including managed services such as RDS, Cloud SQL, Supabase, or Neon. The Bring Your Own PostgreSQL guide covers the one-time role setup.",
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
                    answer: "Yes. Nocturne has a built-in migration tool that connects to your Nightscout, either through its API with your API secret or directly to its MongoDB database, and imports your glucose entries, treatments, device status, and profiles, optionally limited to a date range. It reads from Nightscout and never writes to it.",
                },
                {
                    question: "Will my existing apps still work?",
                    answer: "Yes. Nocturne implements the Nightscout API, so xDrip+, Loop, AndroidAPS, Trio, and other apps that upload to or read from Nightscout work with Nocturne. Point them at your Nocturne URL and give them a token from the Connectors & Apps settings page.",
                },
                {
                    question: "Can I run Nocturne alongside Nightscout?",
                    answer: "Yes. Add your Nightscout site as a connector and Nocturne keeps pulling readings and treatments from it, so both stay current while you move apps over one at a time. Switch the connector off when you are done.",
                },
                {
                    question: "What happens to my Nightscout during migration?",
                    answer: "Nothing. Migration is read-only: it copies data from Nightscout without modifying your original instance. You can keep Nightscout running during and after migration until you are confident in your Nocturne setup.",
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
                    answer: "Connectors are background services that fetch data from sources like Dexcom Share, LibreLinkUp, CareLink, Glooko, or another Nightscout. You sign in to the source once from the Nocturne UI; the connector then checks for new readings and treatments on a schedule and stores them in your instance.",
                },
                {
                    question: "Is there an API?",
                    answer: "Yes. Nocturne implements the Nightscout API (v1, v2, and v3) plus its own v4 endpoints, with OAuth device and PKCE flows for apps. Interactive API documentation is available through Scalar, and official SDKs cover several languages.",
                },
                {
                    question: "Can I contribute to Nocturne?",
                    answer: "Yes. Nocturne is open source and welcomes contributions, and not only code: translations, documentation, and helping other self-hosters in Discord all count. See the Get involved page for tasks you can pick up today.",
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
                    <div class="font-brand text-[12px] font-bold tracking-[0.14em] uppercase text-muted-foreground">0{ci + 1} &middot; {category.title}</div>
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
