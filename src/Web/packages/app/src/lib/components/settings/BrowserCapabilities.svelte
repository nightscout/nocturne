<script lang="ts">
  import { onMount } from "svelte";
  import { Item } from "$lib/components/ui/item";
  import {
    getBrowserCapabilities,
    requestNotificationPermission,
    canVibrate,
    triggerVibration,
    previewAlarmSound,
    stopPreview,
    type BrowserAlarmCapabilities,
  } from "$lib/audio/alarm-sounds";
  import {
    Volume2,
    Bell,
    Vibrate,
    Smartphone,
    Shield,
    Check,
    X,
    AlertTriangle,
    Play,
    Square,
  } from "lucide-svelte";

  let capabilities = $state<BrowserAlarmCapabilities | null>(null);
  let requestingPermission = $state(false);
  let testingAudio = $state(false);
  let testingVibration = $state(false);
  let testingWakeLock = $state(false);
  let wakeLockSentinel = $state<WakeLockSentinel | null>(null);

  onMount(() => {
    capabilities = getBrowserCapabilities();

    return () => {
      // Clean up wake lock if active
      if (wakeLockSentinel) {
        wakeLockSentinel.release();
      }
    };
  });

  async function handleRequestNotificationPermission() {
    requestingPermission = true;
    try {
      await requestNotificationPermission();
      // Refresh capabilities
      capabilities = getBrowserCapabilities();
    } finally {
      requestingPermission = false;
    }
  }

  async function testNotification() {
    if (
      !capabilities?.notifications ||
      capabilities.notificationPermission !== "granted"
    ) {
      return;
    }

    new Notification("Nocturne Test", {
      body: "This is a test notification from Nocturne. Alarms will appear like this!",
      icon: "/images/logo-128.png",
      tag: "test-notification",
    });
  }

  async function testAudio() {
    if (testingAudio) {
      stopPreview();
      testingAudio = false;
      return;
    }

    testingAudio = true;
    try {
      await previewAlarmSound("chime", {
        volume: 50,
        ascending: false,
      });
    } finally {
      testingAudio = false;
    }
  }

  function testVibrate() {
    if (!canVibrate()) return;

    testingVibration = true;
    triggerVibration([200, 100, 200, 100, 200]);

    // Reset state after vibration completes
    setTimeout(() => {
      testingVibration = false;
    }, 800);
  }

  async function testWakeLock() {
    if (!capabilities?.wakeLock) return;

    if (wakeLockSentinel) {
      await wakeLockSentinel.release();
      wakeLockSentinel = null;
      testingWakeLock = false;
      return;
    }

    try {
      wakeLockSentinel = await navigator.wakeLock.request("screen");
      testingWakeLock = true;

      wakeLockSentinel.addEventListener("release", () => {
        wakeLockSentinel = null;
        testingWakeLock = false;
      });

      // Auto-release after 5 seconds
      setTimeout(async () => {
        if (wakeLockSentinel) {
          await wakeLockSentinel.release();
        }
      }, 5000);
    } catch (err) {
      console.error("Wake lock failed:", err);
      testingWakeLock = false;
    }
  }

  function getStatusIcon(supported: boolean, permission?: string) {
    if (!supported) return { icon: X, class: "text-muted-foreground" };
    if (permission === "denied") return { icon: X, class: "text-destructive" };
    if (permission === "default")
      return { icon: AlertTriangle, class: "text-warning" };
    return { icon: Check, class: "text-success" };
  }
</script>

{#if capabilities}
  {@const audioStatus = getStatusIcon(capabilities.audio)}
  {@const AudioStatusIcon = audioStatus.icon}

  {@const notificationStatus = getStatusIcon(
    capabilities.notifications,
    capabilities.notificationPermission
  )}
  {@const NotificationStatusIcon = notificationStatus.icon}
  {@const vibrationStatus = getStatusIcon(capabilities.vibration)}
  {@const VibrationStatusIcon = vibrationStatus.icon}
  {@const wakelockStatus = getStatusIcon(capabilities.wakeLock)}
  {@const WakelockStatusIcon = wakelockStatus.icon}
  <div class="space-y-4 @container">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Smartphone class="h-4 w-4" />
      Browser Capabilities
    </div>

    <div class="grid gap-3 @xl:grid-cols-2">
      <!-- Audio -->
      <Item
        disabled={!capabilities.audio}
        aria-busy={testingAudio}
        onclick={testAudio}
      >
        <div
          class="flex items-center justify-center w-10 h-10 rounded-lg bg-background"
        >
          {#if testingAudio}
            <Square class="h-5 w-5 text-primary fill-primary" />
          {:else}
            <Volume2 class="h-5 w-5" />
          {/if}
        </div>
        <div class="flex-1">
          <div class="flex items-center gap-2">
            <span class="font-medium text-sm">Audio Playback</span>
            {#if !capabilities.audio}
              <span
                class="px-1.5 py-0.5 text-2xs font-medium rounded bg-muted text-muted-foreground"
              >
                Unavailable
              </span>
            {:else}
              <AudioStatusIcon class="h-4 w-4 {audioStatus.class}" />
            {/if}
          </div>
          <p class="text-xs text-muted-foreground">
            {#if testingAudio}
              Playing test sound...
            {:else if capabilities.audio}
              Click to test audio
            {:else}
              Web Audio API not supported
            {/if}
          </p>
        </div>
        {#if capabilities.audio}
          <Play class="h-4 w-4 text-muted-foreground" />
        {/if}
      </Item>

      <!-- Notifications -->
      <Item
        disabled={!capabilities.notifications ||
          capabilities.notificationPermission === "denied"}
        aria-busy={requestingPermission}
        onclick={capabilities.notificationPermission === "granted"
          ? testNotification
          : handleRequestNotificationPermission}
      >
        <div
          class="flex items-center justify-center w-10 h-10 rounded-lg bg-background"
        >
          <Bell class="h-5 w-5" />
        </div>
        <div class="flex-1">
          <div class="flex items-center gap-2">
            <span class="font-medium text-sm">Notifications</span>
            {#if !capabilities.notifications}
              <span
                class="px-1.5 py-0.5 text-2xs font-medium rounded bg-muted text-muted-foreground"
              >
                Unavailable
              </span>
            {:else if capabilities.notificationPermission === "denied"}
              <span
                class="px-1.5 py-0.5 text-2xs font-medium rounded bg-destructive/10 text-destructive"
              >
                Blocked
              </span>
            {:else if capabilities.notificationPermission === "default"}
              <span
                class="px-1.5 py-0.5 text-2xs font-medium rounded bg-warning/10 text-warning"
              >
                Needs Permission
              </span>
            {:else}
              <NotificationStatusIcon
                class="h-4 w-4 {notificationStatus.class}"
              />
            {/if}
          </div>
          <p class="text-xs text-muted-foreground">
            {#if requestingPermission}
              Requesting permission...
            {:else if !capabilities.notifications}
              Notification API not supported
            {:else if capabilities.notificationPermission === "granted"}
              Click to send test
            {:else if capabilities.notificationPermission === "denied"}
              Permission blocked in browser settings
            {:else}
              Click to enable notifications
            {/if}
          </p>
        </div>
        {#if capabilities.notifications && capabilities.notificationPermission !== "denied"}
          <Play class="h-4 w-4 text-muted-foreground" />
        {/if}
      </Item>

      <!-- Vibration -->
      <Item
        disabled={!capabilities.vibration}
        aria-busy={testingVibration}
        onclick={testVibrate}
      >
        <div
          class="flex items-center justify-center w-10 h-10 rounded-lg bg-background"
        >
          <Vibrate class="h-5 w-5" />
        </div>
        <div class="flex-1">
          <div class="flex items-center gap-2">
            <span class="font-medium text-sm">Vibration</span>
            {#if !capabilities.vibration}
              <span
                class="px-1.5 py-0.5 text-2xs font-medium rounded bg-muted text-muted-foreground"
              >
                Unavailable
              </span>
            {:else}
              <VibrationStatusIcon class="h-4 w-4 {vibrationStatus.class}" />
            {/if}
          </div>
          <p class="text-xs text-muted-foreground">
            {#if testingVibration}
              Vibrating...
            {:else if capabilities.vibration}
              Click to test vibration
            {:else}
              Only available on mobile devices
            {/if}
          </p>
        </div>
        {#if capabilities.vibration}
          <Play class="h-4 w-4 text-muted-foreground" />
        {/if}
      </Item>

      <!-- Wake Lock -->
      <Item
        disabled={!capabilities.wakeLock}
        aria-busy={testingWakeLock}
        onclick={testWakeLock}
      >
        <div
          class="flex items-center justify-center w-10 h-10 rounded-lg bg-background"
        >
          <Shield class="h-5 w-5" />
        </div>
        <div class="flex-1">
          <div class="flex items-center gap-2">
            <span class="font-medium text-sm">Screen Wake Lock</span>
            {#if !capabilities.wakeLock}
              <span
                class="px-1.5 py-0.5 text-2xs font-medium rounded bg-muted text-muted-foreground"
              >
                Unavailable
              </span>
            {:else}
              <WakelockStatusIcon class="h-4 w-4 {wakelockStatus.class}" />
            {/if}
          </div>
          <p class="text-xs text-muted-foreground">
            {#if testingWakeLock}
              Active (releases in 5s)
            {:else if capabilities.wakeLock}
              Click to test (5 seconds)
            {:else}
              Wake Lock API not supported
            {/if}
          </p>
        </div>
        {#if capabilities.wakeLock}
          {#if testingWakeLock}
            <Square class="h-4 w-4 text-primary" />
          {:else}
            <Play class="h-4 w-4 text-muted-foreground" />
          {/if}
        {/if}
      </Item>
    </div>

    <p class="text-xs text-muted-foreground">
      Click each capability to test it. These determine what alarm features are
      available in your browser.
    </p>
  </div>
{/if}
