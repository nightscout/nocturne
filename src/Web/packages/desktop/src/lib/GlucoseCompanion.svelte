<script lang="ts">
  import { invoke } from "@tauri-apps/api/core";
  import { listen } from "@tauri-apps/api/event";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "@nocturne/ui/ui/card";
  import { Button } from "@nocturne/ui/ui/button";
  import { Input } from "@nocturne/ui/ui/input";
  import { Label } from "@nocturne/ui/ui/label";
  import { Alert, AlertDescription } from "@nocturne/ui/ui/alert";
  import { Activity, CheckCircle2, Loader2 } from "@lucide/svelte";

  // Matches the camelCase DeviceFlowInfo returned by the `companion_link_start` Rust command.
  type DeviceFlowInfo = {
    userCode: string;
    verificationUri: string;
    verificationUriComplete?: string | null;
    intervalSecs: number;
    expiresInSecs: number;
  };

  type Phase = "input" | "waiting" | "linked";

  let phase = $state<Phase>("input");
  let server = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let flow = $state<DeviceFlowInfo | null>(null);
  let updatedAt = $state<number | null>(null);

  const expiryMinutes = $derived(
    flow ? Math.max(1, Math.round(flow.expiresInSecs / 60)) : 0,
  );
  // Prefer the URL that embeds the code so the user doesn't have to type it.
  const openUrl = $derived(flow?.verificationUriComplete ?? flow?.verificationUri ?? null);

  function describeError(e: unknown): string {
    if (typeof e === "string") return e;
    if (e && typeof e === "object" && "message" in e) {
      return String((e as { message: unknown }).message);
    }
    return "Something went wrong.";
  }

  async function refreshLinked() {
    try {
      const linked = await invoke<boolean>("companion_is_linked");
      phase = linked ? "linked" : phase === "linked" ? "input" : phase;
    } catch (e) {
      error = describeError(e);
    }
  }

  async function connect() {
    busy = true;
    error = null;
    try {
      // `companion_link_start` also begins background polling; the UI only shows the code
      // and reacts to the `companion-linked` / `companion-link-failed` events.
      flow = await invoke<DeviceFlowInfo>("companion_link_start", { server });
      phase = "waiting";
    } catch (e) {
      error = describeError(e);
    } finally {
      busy = false;
    }
  }

  async function unlink() {
    busy = true;
    error = null;
    try {
      await invoke("companion_unlink");
      flow = null;
      updatedAt = null;
      await refreshLinked();
      if (phase !== "linked") phase = "input";
    } catch (e) {
      error = describeError(e);
    } finally {
      busy = false;
    }
  }

  // Reflect whatever link state the Rust side already holds.
  $effect(() => {
    refreshLinked();
  });

  $effect(() => {
    const unlistenLinked = listen("companion-linked", () => {
      flow = null;
      error = null;
      phase = "linked";
      refreshLinked();
    });
    const unlistenFailed = listen<string>("companion-link-failed", (event) => {
      error = event.payload || "Linking failed. Please try again.";
      flow = null;
      phase = "input";
    });
    const unlistenUpdated = listen("glucose-updated", () => {
      updatedAt = Date.now();
    });
    return () => {
      unlistenLinked.then((fn) => fn());
      unlistenFailed.then((fn) => fn());
      unlistenUpdated.then((fn) => fn());
    };
  });
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <Activity class="h-4 w-4" /> Glucose companion
    </CardTitle>
    <CardDescription>
      Sync your latest glucose reading to your taskbar from your Nocturne site.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    {#if error}
      <Alert variant="destructive">
        <AlertDescription>{error}</AlertDescription>
      </Alert>
    {/if}

    {#if phase === "input"}
      <div class="space-y-2">
        <Label for="companion-server">Server URL</Label>
        <Input
          id="companion-server"
          bind:value={server}
          placeholder="https://<your-tenant>.nocturne.run"
          disabled={busy}
        />
      </div>
      <Button onclick={connect} disabled={busy || !server.trim()}>
        {#if busy}
          <Loader2 class="mr-1 h-3.5 w-3.5 animate-spin" /> Connecting…
        {:else}
          Connect glucose
        {/if}
      </Button>
    {/if}

    {#if phase === "waiting" && flow}
      <div class="space-y-3">
        <p class="text-sm">
          Enter this code to approve the companion on your Nocturne site:
        </p>
        <p class="text-foreground text-center font-mono text-2xl font-semibold tracking-widest">
          {flow.userCode}
        </p>
        {#if openUrl}
          <!-- Plain anchor opens in the default handler; no Tauri opener plugin wired up. -->
          <a
            href={openUrl}
            target="_blank"
            rel="noreferrer"
            class="text-primary block break-all text-center text-sm underline"
          >
            {openUrl}
          </a>
        {/if}
        <p class="text-muted-foreground text-center text-xs">
          Code expires in {expiryMinutes} minute{expiryMinutes === 1 ? "" : "s"}.
        </p>
        <div class="flex items-center justify-center gap-2">
          <Loader2 class="text-primary h-4 w-4 animate-spin" />
          <span class="text-muted-foreground text-xs">Waiting for approval…</span>
        </div>
      </div>
    {/if}

    {#if phase === "linked"}
      <div class="flex items-start gap-2">
        <CheckCircle2 class="mt-0.5 h-5 w-5 shrink-0 text-green-600" />
        <div class="text-sm">
          <p class="font-medium">Connected — glucose is syncing to your taskbar.</p>
          {#if updatedAt}
            <p class="text-muted-foreground">Last updated just now.</p>
          {/if}
        </div>
      </div>
      <Button variant="outline" size="sm" onclick={unlink} disabled={busy}>
        {busy ? "Unlinking…" : "Unlink"}
      </Button>
    {/if}
  </CardContent>
</Card>
