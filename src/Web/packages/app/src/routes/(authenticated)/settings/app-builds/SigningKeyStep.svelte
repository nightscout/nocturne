<script lang="ts">
  import { onDestroy } from "svelte";
  import Download from "@lucide/svelte/icons/download";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import Upload from "@lucide/svelte/icons/upload";
  import type { SealedAppBuildSecret } from "#lib/api/index.js";
  import type {
    KeystoreFormat,
    SigningKeystore,
  } from "#lib/app-builds/keystore.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import {
    getAppBuildSecretKey,
    putAppBuildSecret,
  } from "#lib/api/generated/appBuilds.generated.remote.js";
  import { fresh } from "#lib/api/fresh-query.js";
  import { errorStatus } from "#lib/forms/submit-error.js";
  import {
    chooseKeyAgainText,
    describeError,
    importProblemText,
    newPasswordText,
  } from "./app-builds-copy.svelte";

  interface Props {
    targetId: string;
    /** The target's signing-key secret (`KEYSTORE_SET` for AAPS). */
    secretName: string;
    /** The app's display name. */
    appName: string;
    onSaved?: () => void;
  }

  let { targetId, secretName, appName, onSaved }: Props = $props();

  // Loaded only here, so node-forge and libsodium stay out of every other page's bundle.
  const keystoreLibrary = () => import("#lib/app-builds/keystore.js");
  const sealLibrary = () => import("#lib/app-builds/sealed-secret.js");

  type Mode = "import" | "create";
  let mode = $state<Mode | null>(null);

  // The plaintext keystore and file bytes live outside reactive state, so nothing renders, proxies
  // or serializes them, and `forget()` can overwrite them.
  let held: SigningKeystore | null = null;
  let fileBytes: Uint8Array | null = null;

  /**
   * Bumped whenever the selection changes or the step is left. An async read,
   * check or key that finishes under an older generation belongs to a selection
   * that is gone, so it is zeroed and dropped.
   */
  let generation = 0;

  /**
   * What is held: a checked or unchecked import, or a new key. Null while
   * nothing is held.
   */
  let heldKind = $state<"checked" | "unchecked" | "created" | null>(null);

  let fileInput = $state<HTMLInputElement | null>(null);
  let format = $state<KeystoreFormat | null>(null);
  let storePassword = $state("");
  let alias = $state("");
  let sameKeyPassword = $state(true);
  let keyPassword = $state("");

  let newPassword = $state("");
  let newPasswordAgain = $state("");
  let backupDownloaded = $state(false);

  let working = $state(false);
  let message = $state<string | null>(null);

  /**
   * The sealed value, kept so a send that failed for a reason a retry can fix
   * is sent again without the plaintext. `newKey` says what the user chooses
   * again if it is dropped.
   */
  let sealed = $state<(SealedAppBuildSecret & { newKey: boolean }) | null>(
    null
  );
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  const busy = $derived(working || saving);
  const canSave = $derived(
    !busy &&
      (sealed !== null ||
        heldKind === "checked" ||
        heldKind === "unchecked" ||
        (heldKind === "created" && backupDownloaded))
  );

  function forget() {
    generation++;
    held?.bytes.fill(0);
    fileBytes?.fill(0);
    held = null;
    fileBytes = null;
    heldKind = null;
    storePassword = "";
    alias = "";
    keyPassword = "";
    newPassword = "";
    newPasswordAgain = "";
    format = null;
    if (fileInput) fileInput.value = "";
  }

  onDestroy(forget);

  function choose(next: Mode) {
    forget();
    sealed = null;
    message = null;
    saveError = null;
    backupDownloaded = false;
    mode = next;
  }

  async function readFile(event: Event & { currentTarget: HTMLInputElement }) {
    const file = event.currentTarget.files?.[0];
    const mine = ++generation;
    fileBytes?.fill(0);
    held = null;
    heldKind = null;
    message = null;
    format = null;
    fileBytes = null;
    if (!file) return;

    const bytes = new Uint8Array(await file.arrayBuffer());
    const { keystoreFormat } = await keystoreLibrary();
    if (mine !== generation) {
      bytes.fill(0);
      return;
    }
    fileBytes = bytes;
    format = keystoreFormat(bytes);
    if (!format) {
      message = importProblemText("not_keystore");
    } else if (format.aliases.length === 1 || format.kind === "jks") {
      alias = format.aliases[0] ?? "";
    }
  }

  /**
   * A change to a checked field makes the held keystore stale until it is
   * checked again.
   */
  function dropCheck() {
    if (heldKind !== "checked" && heldKind !== "unchecked") return;
    held = null;
    heldKind = null;
  }

  async function checkImport() {
    if (!fileBytes) return;
    const mine = generation;
    working = true;
    message = null;
    try {
      const { importKeystore } = await keystoreLibrary();
      const result = await importKeystore(
        fileBytes,
        storePassword,
        alias,
        sameKeyPassword ? storePassword : keyPassword
      );
      if (mine !== generation) {
        if (result.ok) result.keystore.bytes.fill(0);
        return;
      }
      if (result.ok) {
        held = result.keystore;
        heldKind = result.checked ? "checked" : "unchecked";
      } else {
        held = null;
        heldKind = null;
        message = importProblemText(result.problem, result.aliases);
      }
    } catch {
      if (mine === generation)
        message =
          "Nocturne could not read the keystore in this browser. Try again.";
    } finally {
      working = false;
    }
  }

  async function createKey() {
    const mine = generation;
    working = true;
    message = null;
    try {
      const { createKeystore, newPasswordProblem } = await keystoreLibrary();
      const problem = newPasswordProblem(newPassword, newPasswordAgain);
      if (problem) {
        message = newPasswordText(problem);
        return;
      }
      const created = await createKeystore(newPassword);
      if (mine !== generation) {
        created.bytes.fill(0);
        return;
      }
      held = created;
      heldKind = "created";
      backupDownloaded = false;
    } catch {
      if (mine === generation)
        message = "Nocturne could not make the key in this browser. Try again.";
    } finally {
      working = false;
    }
  }

  async function downloadBackup() {
    if (!held) return;
    const { BACKUP_FILE_NAME } = await keystoreLibrary();
    if (!held) return;
    const url = URL.createObjectURL(
      new Blob([held.bytes.slice()], { type: "application/octet-stream" })
    );
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = BACKUP_FILE_NAME;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    backupDownloaded = true;
  }

  /**
   * A 5xx, a 429 or no answer at all; any other refusal is the same on a second
   * send.
   */
  function retryable(err: unknown): boolean {
    const status = errorStatus(err);
    return status === undefined || status === 429 || status >= 500;
  }

  async function save() {
    if (!canSave) return;
    saving = true;
    saveError = null;
    try {
      if (!sealed) {
        if (!held) return;
        const [{ packKeystoreSet }, { sealSecret }] = await Promise.all([
          keystoreLibrary(),
          sealLibrary(),
        ]);
        const key = await fresh(getAppBuildSecretKey(targetId));
        if (!key.key || !key.keyId) throw new Error("no_public_key");
        const encryptedValue = await sealSecret(packKeystoreSet(held), key.key);
        sealed = {
          encryptedValue,
          keyId: key.keyId,
          newKey: heldKind === "created",
        };
        forget();
      }
      await putAppBuildSecret({
        targetId,
        name: secretName,
        request: { encryptedValue: sealed.encryptedValue, keyId: sealed.keyId },
      });
      sealed = null;
      mode = null;
      onSaved?.();
    } catch (err) {
      saveError = describeError(err);
      if (sealed && !retryable(err)) {
        saveError = `${saveError} ${chooseKeyAgainText(sealed.newKey)}`;
        sealed = null;
        mode = null;
      }
    } finally {
      saving = false;
    }
  }
</script>

<div class="space-y-4">
  <div class="space-y-2 text-sm">
    <p>
      The build signs the app with your key. Your browser locks the key with the
      public key of your repository before it sends it. The Nocturne server does
      not receive the key or its passwords.
    </p>
    <p class="font-medium">
      If {appName} is already installed on your phone, import the key that you use
      now. Only an app signed with the same key can update the installed app.
    </p>
  </div>

  <div class="flex flex-wrap gap-2">
    <Button
      variant={mode === "import" ? "default" : "outline"}
      disabled={busy}
      onclick={() => choose("import")}
    >
      <Upload class="h-4 w-4" />
      Import my key
    </Button>
    <Button
      variant={mode === "create" ? "default" : "outline"}
      disabled={busy}
      onclick={() => choose("create")}
    >
      <KeyRound class="h-4 w-4" />
      Make a new key
    </Button>
  </div>

  {#if mode === "import" && !sealed}
    <div class="space-y-3 rounded-lg border p-4">
      <div class="space-y-2">
        <Label for="keystore-file">
          Keystore file (.jks, .keystore, .p12 or .pfx)
        </Label>
        <Input
          id="keystore-file"
          bind:ref={fileInput}
          type="file"
          accept=".jks,.keystore,.p12,.pfx"
          disabled={busy}
          onchange={readFile}
        />
      </div>
      {#if format}
        <div class="space-y-2">
          <Label for="keystore-password">Keystore password</Label>
          <Input
            id="keystore-password"
            type="password"
            autocomplete="off"
            bind:value={storePassword}
            oninput={dropCheck}
          />
        </div>
        <div class="space-y-2">
          <Label for="keystore-alias">Key alias</Label>
          {#if format.kind === "jks" && format.aliases.length > 0}
            <Select.Root
              type="single"
              bind:value={alias}
              onValueChange={dropCheck}
            >
              <Select.Trigger id="keystore-alias" class="w-full max-w-md">
                {alias}
              </Select.Trigger>
              <Select.Content>
                {#each format.aliases as option (option)}
                  <Select.Item value={option} label={option} />
                {/each}
              </Select.Content>
            </Select.Root>
          {:else}
            <Input
              id="keystore-alias"
              autocomplete="off"
              spellcheck={false}
              bind:value={alias}
              oninput={dropCheck}
            />
          {/if}
        </div>
        <div class="flex items-center gap-2">
          <Checkbox
            id="same-key-password"
            bind:checked={sameKeyPassword}
            onCheckedChange={dropCheck}
          />
          <Label for="same-key-password">
            The key password is the same as the keystore password
          </Label>
        </div>
        {#if !sameKeyPassword}
          <div class="space-y-2">
            <Label for="key-password">Key password</Label>
            <Input
              id="key-password"
              type="password"
              autocomplete="off"
              bind:value={keyPassword}
              oninput={dropCheck}
            />
          </div>
        {/if}
        <Button onclick={checkImport} disabled={busy}>
          {#if working}<Loader2 class="h-4 w-4 animate-spin" />{/if}
          Check the key
        </Button>
      {/if}
      {#if heldKind === "checked"}
        <p role="status" class="text-sm">
          The keystore and the passwords are correct.
        </p>
      {:else if heldKind === "unchecked"}
        <p role="status" class="text-sm">
          Nocturne could not fully read this keystore. The build checks the
          passwords.
        </p>
      {/if}
    </div>
  {/if}

  {#if mode === "create" && !sealed}
    <div class="space-y-3 rounded-lg border p-4">
      {#if heldKind !== "created"}
        <div class="space-y-2">
          <Label for="new-key-password">Password for the new key</Label>
          <Input
            id="new-key-password"
            type="password"
            autocomplete="new-password"
            bind:value={newPassword}
          />
        </div>
        <div class="space-y-2">
          <Label for="new-key-password-again">Type the password again</Label>
          <Input
            id="new-key-password-again"
            type="password"
            autocomplete="new-password"
            bind:value={newPasswordAgain}
          />
        </div>
        <Button onclick={createKey} disabled={busy}>
          {#if working}<Loader2 class="h-4 w-4 animate-spin" />{/if}
          Make the key
        </Button>
      {:else}
        <Alert.Root variant="warning">
          <TriangleAlert />
          <Alert.Title>CAUTION</Alert.Title>
          <Alert.Description>
            <p>
              Download the backup file and keep it with the password in a safe
              place. Nocturne cannot recover the key or its password.
            </p>
            <p>
              The alias of the key is key0. Write it down with the password.
            </p>
            <p>
              Each later update of the app must be signed with this key. An app
              signed with a different key cannot update the installed app.
            </p>
          </Alert.Description>
        </Alert.Root>
        <Button variant="outline" disabled={busy} onclick={downloadBackup}>
          <Download class="h-4 w-4" />
          Download the backup file
        </Button>
        {#if !backupDownloaded}
          <p class="text-sm text-muted-foreground">
            Download the backup file before you save the key.
          </p>
        {/if}
      {/if}
    </div>
  {/if}

  {#if message}
    <p role="alert" class="text-sm text-destructive">{message}</p>
  {/if}

  {#if heldKind || sealed}
    <Button onclick={save} disabled={!canSave}>
      {#if saving}<Loader2 class="h-4 w-4 animate-spin" />{/if}
      Save the key to GitHub
    </Button>
  {/if}
  {#if saveError}
    <p role="alert" class="text-sm text-destructive">{saveError}</p>
  {/if}
</div>
