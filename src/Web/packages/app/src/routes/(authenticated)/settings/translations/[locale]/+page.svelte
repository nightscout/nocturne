<script lang="ts">
  import { page } from "$app/state";
  import { browser } from "$app/environment";
  import { beforeNavigate } from "$app/navigation";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { ArrowLeft, GitPullRequest, Loader2, Trash2 } from "@lucide/svelte";
  import TranslationEditor from "@nocturne/cms/translations/TranslationEditor.svelte";
  import {
    buildMessages,
    messageKey,
    parsePo,
    type TranslationMessage,
  } from "@nocturne/cms/translations";
  import * as translationsApi from "$api/generated/translations.generated.remote";
  import {
    getLanguageLabel,
    isSupportedLocale,
    type SupportedLocale,
  } from "$lib/stores/appearance-store.svelte";
  import { SvelteMap } from "svelte/reactivity";

  // Catalogs are read from the upstream repo so drafts are always edited
  // against the same base the contribution PR will be applied to.
  const CATALOG_BASE =
    "https://raw.githubusercontent.com/nightscout/nocturne/main/src/Web/locales";

  const locale = $derived(page.params.locale ?? "");
  const localeValid = $derived(isSupportedLocale(locale) && locale !== "en");

  let messages = $state<TranslationMessage[]>([]);
  let catalogError = $state<string | null>(null);
  let catalogLoading = $state(true);

  const draftsQuery = $derived(
    localeValid ? translationsApi.getDrafts({ locale }) : null,
  );

  const drafts = new SvelteMap<string, string[]>();
  let serverSeeded = $state(false);
  $effect(() => {
    const current = draftsQuery?.current;
    if (!current || serverSeeded) return;
    for (const d of current) {
      // msgId/translations are required on the server model but the generated
      // client types every field as optional.
      if (!d.msgId || !d.translations) continue;
      const key = messageKey(d.context ?? "", d.msgId);
      // A local edit made before the server drafts arrived wins.
      if (!pendingByLocale.get(locale)?.has(key)) {
        drafts.set(key, d.translations);
      }
    }
    serverSeeded = true;
  });

  // Client-side navigation between locales reuses this component: flush the
  // old locale's queued edits under the OLD locale, then reset all state so
  // one locale's drafts can never appear under, or be saved to, another.
  let activeLocale = $state("");
  $effect(() => {
    const next = locale;
    if (next === activeLocale) return;
    const prev = activeLocale;
    activeLocale = next;
    if (prev.length > 0) {
      void flushNow(prev);
      drafts.clear();
      serverSeeded = false;
      saveState = "idle";
      messages = [];
    }
    // Edits whose save failed while this locale was last open are still
    // queued under it: show them again and retry the save.
    const queued = pendingByLocale.get(next);
    if (queued && queued.size > 0) {
      for (const [key, item] of queued) {
        if (item.values !== null) drafts.set(key, item.values);
      }
      if (flushTimer) clearTimeout(flushTimer);
      flushTimer = setTimeout(() => void flushNow(next), 800);
    }
  });

  beforeNavigate(() => {
    // The command keeps running after unmount; only the batch capture must
    // happen before the component goes away.
    if (activeLocale.length > 0) void flushNow(activeLocale);
  });

  let fetchSeq = 0;
  $effect(() => {
    if (!browser || !localeValid) return;
    const seq = ++fetchSeq;
    const target = locale;
    catalogLoading = true;
    catalogError = null;
    Promise.all(
      ["en", target].map(async (l) => {
        const res = await fetch(`${CATALOG_BASE}/${l}.po`);
        if (!res.ok) throw new Error(`Failed to load the ${l} catalog (${res.status})`);
        return parsePo(await res.text());
      }),
    )
      .then(([source, targetCatalog]) => {
        if (seq !== fetchSeq) return;
        messages = buildMessages(source, targetCatalog);
      })
      .catch((e) => {
        if (seq !== fetchSeq) return;
        catalogError = e instanceof Error ? e.message : "Failed to load catalogs";
      })
      .finally(() => {
        if (seq === fetchSeq) catalogLoading = false;
      });
  });

  // Autosave: queue changed keys, flush as one batch upsert. Flushes are
  // serialized on a promise chain; the batch (and its locale) is captured
  // synchronously at flush time so a locale switch or navigation cannot
  // re-label queued edits. Queues are per-locale so a batch that fails after
  // the user has already moved on is retried on the next flush of the locale
  // it belongs to instead of being dropped.
  type PendingEdit = { message: TranslationMessage; values: string[] | null; seq: number };
  const pendingByLocale = new Map<string, Map<string, PendingEdit>>();
  const latestSeqByLocale = new Map<string, Map<string, number>>();
  let editSeq = 0;
  let flushTimer: ReturnType<typeof setTimeout> | null = null;
  let flushChain: Promise<void> = Promise.resolve();
  let saveState = $state<"idle" | "saving" | "error">("idle");

  function perLocale<V>(
    store: Map<string, Map<string, V>>,
    forLocale: string,
  ): Map<string, V> {
    let inner = store.get(forLocale);
    if (!inner) {
      inner = new Map();
      store.set(forLocale, inner);
    }
    return inner;
  }

  function onDraft(message: TranslationMessage, values: string[] | null) {
    if (values === null) drafts.delete(message.key);
    else drafts.set(message.key, values);
    const forLocale = locale;
    const seq = ++editSeq;
    perLocale(pendingByLocale, forLocale).set(message.key, { message, values, seq });
    perLocale(latestSeqByLocale, forLocale).set(message.key, seq);
    if (flushTimer) clearTimeout(flushTimer);
    flushTimer = setTimeout(() => void flushNow(forLocale), 800);
  }

  function flushNow(forLocale: string): Promise<void> {
    if (flushTimer) {
      clearTimeout(flushTimer);
      flushTimer = null;
    }
    const queue = pendingByLocale.get(forLocale);
    if (!queue || queue.size === 0) return flushChain;
    const batch = [...queue.values()];
    queue.clear();
    flushChain = flushChain.then(() => sendBatch(forLocale, batch));
    return flushChain;
  }

  async function sendBatch(forLocale: string, batch: PendingEdit[]) {
    // A partly-filled plural draft cannot be stored (the API requires
    // non-empty values); it stays local-only until completed.
    const isCleared = (values: string[] | null) =>
      values === null || values.every((v) => v.length === 0);
    const isStorable = (values: string[] | null) =>
      isCleared(values) || !values!.some((v) => v.length === 0);

    const entries = batch
      .filter(({ values }) => isStorable(values))
      .map(({ message, values }) => ({
        msgId: message.msgid,
        context: message.context.length === 0 ? null : message.context,
        translations: isCleared(values) ? [] : values!,
      }));
    if (entries.length === 0) return;
    if (forLocale === activeLocale) saveState = "saving";
    try {
      await translationsApi.upsertDrafts({ locale: forLocale, entries });
      if (forLocale === activeLocale) saveState = "idle";
    } catch {
      // Re-queue under the locale the edits were made in, so returning to that
      // locale retries them. The status line only speaks for the locale on
      // screen.
      const queue = perLocale(pendingByLocale, forLocale);
      const latest = perLocale(latestSeqByLocale, forLocale);
      for (const item of batch) {
        // Skip keys a newer edit has superseded, whether it is still queued or
        // already went out in a later batch that succeeded. Re-queuing those
        // would send the older value over the newer one.
        if ((latest.get(item.message.key) ?? item.seq) > item.seq) continue;
        queue.set(item.message.key, item);
      }
      if (forLocale === activeLocale) saveState = "error";
    }
  }

  // Submission
  let submitOpen = $state(false);
  let submitting = $state(false);
  let submitError = $state<string | null>(null);
  let submitResult = $state<{ prUrl: string; applied: number; remaining: number } | null>(null);
  let contributorName = $state("");
  let contributorGitHub = $state("");
  let contributorEmail = $state("");
  let note = $state("");

  const CONTRIBUTOR_STORAGE_KEY = "nocturne-translation-contributor";
  $effect(() => {
    if (!browser) return;
    try {
      const stored = localStorage.getItem(CONTRIBUTOR_STORAGE_KEY);
      if (stored) {
        const parsed = JSON.parse(stored);
        contributorName = parsed.name ?? "";
        contributorGitHub = parsed.gitHubUsername ?? "";
        contributorEmail = parsed.email ?? "";
      }
    } catch {
      // Ignore malformed stored contributor info.
    }
  });

  async function submit() {
    await flushNow(locale);
    if (saveState === "error") {
      submitError = "Some drafts failed to save. Try again.";
      return;
    }
    submitting = true;
    submitError = null;
    try {
      localStorage.setItem(
        CONTRIBUTOR_STORAGE_KEY,
        JSON.stringify({
          name: contributorName,
          gitHubUsername: contributorGitHub,
          email: contributorEmail,
        }),
      );
      const result = await translationsApi.submitDrafts({
        locale,
        contributor: {
          name: contributorName,
          gitHubUsername: contributorGitHub.length ? contributorGitHub : null,
          email: contributorEmail.length ? contributorEmail : null,
        },
        note: note.length ? note : null,
      });
      submitResult = {
        prUrl: result.contribution?.prUrl ?? "",
        applied: result.contribution?.applied ?? 0,
        remaining: result.remainingDrafts ?? 0,
      };
      // Applied drafts were deleted server-side; reseed from the server. The
      // routes carry no Invalidates (see TranslationsController.UpsertDrafts),
      // so refresh the per-locale query instance explicitly.
      drafts.clear();
      serverSeeded = false;
      await draftsQuery?.refresh();
    } catch (e) {
      submitError =
        (e as { message?: string })?.message ??
        "Failed to submit the contribution.";
    } finally {
      submitting = false;
    }
  }

  let clearOpen = $state(false);
  let clearError = $state<string | null>(null);
  async function clearAll() {
    clearError = null;
    try {
      await translationsApi.clearDrafts({ locale });
      pendingByLocale.get(locale)?.clear();
      if (flushTimer) {
        clearTimeout(flushTimer);
        flushTimer = null;
      }
      drafts.clear();
      serverSeeded = false;
      await draftsQuery?.refresh();
      clearOpen = false;
    } catch (e) {
      clearError =
        (e as { message?: string })?.message ?? "Failed to clear drafts.";
    }
  }
</script>

<svelte:head>
  <title>Translate {localeValid ? getLanguageLabel(locale as SupportedLocale) : ""} - Settings</title>
</svelte:head>

{#if !localeValid}
  <p class="text-muted-foreground">Unknown locale.</p>
{:else}
  <div class="space-y-6">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <a
          href="/settings/translations"
          class="mb-1 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft class="h-3.5 w-3.5" />
          Translations
        </a>
        <h1 class="text-2xl font-bold">
          {getLanguageLabel(locale as SupportedLocale, locale as SupportedLocale)}
          <span class="text-muted-foreground font-normal">
            · {getLanguageLabel(locale as SupportedLocale)}
          </span>
        </h1>
      </div>
      <div class="flex items-center gap-2">
        {#if saveState === "saving"}
          <span class="text-sm text-muted-foreground">Saving…</span>
        {:else if saveState === "error"}
          <span class="text-sm text-destructive">Draft save failed — edits retry on next change</span>
        {/if}
        <Button
          variant="outline"
          disabled={drafts.size === 0}
          onclick={() => (clearOpen = true)}
        >
          <Trash2 class="mr-1 h-4 w-4" />
          Clear drafts
        </Button>
        <Button
          disabled={drafts.size === 0}
          onclick={() => {
            submitResult = null;
            submitError = null;
            submitOpen = true;
          }}
        >
          <GitPullRequest class="mr-1 h-4 w-4" />
          Submit {drafts.size || ""} draft{drafts.size === 1 ? "" : "s"}
        </Button>
      </div>
    </div>

    {#if catalogLoading}
      <div class="flex items-center gap-2 py-12 justify-center text-muted-foreground">
        <Loader2 class="h-4 w-4 animate-spin" />
        Loading catalogs…
      </div>
    {:else if catalogError}
      <p class="py-12 text-center text-sm text-destructive">{catalogError}</p>
    {:else}
      <TranslationEditor {messages} {drafts} ondraft={onDraft} />
    {/if}
  </div>

  <Dialog.Root bind:open={submitOpen}>
    <Dialog.Content class="sm:max-w-lg">
      {#if submitResult}
        <Dialog.Header>
          <Dialog.Title>Contribution submitted</Dialog.Title>
          <Dialog.Description>
            {submitResult.applied} translation{submitResult.applied === 1 ? "" : "s"} proposed
            {#if submitResult.remaining > 0}
              · {submitResult.remaining} draft{submitResult.remaining === 1 ? "" : "s"} kept
            {/if}
          </Dialog.Description>
        </Dialog.Header>
        {#if submitResult.prUrl}
          <a
            href={submitResult.prUrl}
            target="_blank"
            rel="noopener noreferrer"
            class="text-sm text-primary underline underline-offset-4"
          >
            View the pull request
          </a>
        {/if}
        <Dialog.Footer>
          <Button onclick={() => (submitOpen = false)}>Done</Button>
        </Dialog.Footer>
      {:else}
        <Dialog.Header>
          <Dialog.Title>Submit translations</Dialog.Title>
          <Dialog.Description>
            Your drafts are proposed to the Nocturne project as a pull request.
            Your name appears in the commit credit.
          </Dialog.Description>
        </Dialog.Header>
        <div class="space-y-3">
          <div class="space-y-1">
            <Label for="contrib-name">Name</Label>
            <Input id="contrib-name" bind:value={contributorName} placeholder="Your name" />
          </div>
          <div class="space-y-1">
            <Label for="contrib-github">GitHub username (optional)</Label>
            <Input id="contrib-github" bind:value={contributorGitHub} placeholder="octocat" />
            <p class="text-xs text-muted-foreground">
              Used for commit co-author credit.
            </p>
          </div>
          <div class="space-y-1">
            <Label for="contrib-email">Email (optional)</Label>
            <Input id="contrib-email" type="email" bind:value={contributorEmail} />
          </div>
          <div class="space-y-1">
            <Label for="contrib-note">Note to reviewers (optional)</Label>
            <Textarea id="contrib-note" bind:value={note} rows={3} />
          </div>
          {#if submitError}
            <p class="text-sm text-destructive">{submitError}</p>
          {/if}
        </div>
        <Dialog.Footer>
          <Button variant="outline" onclick={() => (submitOpen = false)} disabled={submitting}>
            Cancel
          </Button>
          <Button onclick={submit} disabled={submitting || contributorName.trim().length === 0}>
            {#if submitting}
              <Loader2 class="mr-1 h-4 w-4 animate-spin" />
            {/if}
            Submit
          </Button>
        </Dialog.Footer>
      {/if}
    </Dialog.Content>
  </Dialog.Root>

  <AlertDialog.Root bind:open={clearOpen}>
    <AlertDialog.Content>
      <AlertDialog.Header>
        <AlertDialog.Title>Clear all drafts?</AlertDialog.Title>
        <AlertDialog.Description>
          All {drafts.size} draft{drafts.size === 1 ? "" : "s"} for
          {getLanguageLabel(locale as SupportedLocale)} will be deleted. This cannot be undone.
        </AlertDialog.Description>
      </AlertDialog.Header>
      {#if clearError}
        <p class="text-sm text-destructive">{clearError}</p>
      {/if}
      <AlertDialog.Footer>
        <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
        <!-- Plain Button instead of AlertDialog.Action: the dialog must stay
             open to show the error when the server delete fails. -->
        <Button onclick={() => void clearAll()}>Clear drafts</Button>
      </AlertDialog.Footer>
    </AlertDialog.Content>
  </AlertDialog.Root>
{/if}
