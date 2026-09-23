import { randomUUID } from "$lib/utils";

/**
 * Alarm Sound Generator and Player
 *
 * Uses native browser APIs:
 * - Web Audio API for sound generation
 * - Notifications API for system notifications
 * - Vibration API for haptic feedback (mobile)
 * - Screen Wake Lock API to prevent sleep during alarms
 *
 * This ensures consistent cross-platform audio without external file dependencies.
 */

export interface AlarmSoundOptions {
  volume?: number; // 0-100
  ascending?: boolean;
  startVolume?: number; // 0-100, for ascending
  ascendDurationSeconds?: number;
  vibrate?: boolean;
  vibrationPattern?: number[]; // [vibrate, pause, vibrate, ...] in ms
  showNotification?: boolean;
  notificationTitle?: string;
  notificationBody?: string;
  preventSleep?: boolean;
  minDurationSeconds?: number;
}

type OscillatorType = 'sine' | 'square' | 'sawtooth' | 'triangle';

interface TonePattern {
  frequency: number;
  duration: number;
  type: OscillatorType;
  ramp?: { to: number; duration: number };
}

interface AlarmDefinition {
  name: string;
  description: string;
  patterns: TonePattern[];
  repeatDelay: number; // ms between pattern repeats
  loops: number; // how many times to play the pattern
}

// Alarm sound definitions
const ALARM_DEFINITIONS: Record<string, AlarmDefinition> = {
  'alarm-urgent': {
    name: 'Urgent Alarm',
    description: 'Loud, attention-grabbing alarm for critical alerts',
    patterns: [
      { frequency: 880, duration: 150, type: 'square' },
      { frequency: 0, duration: 50, type: 'sine' }, // silence
      { frequency: 1100, duration: 150, type: 'square' },
      { frequency: 0, duration: 50, type: 'sine' },
      { frequency: 880, duration: 150, type: 'square' },
      { frequency: 0, duration: 50, type: 'sine' },
      { frequency: 1100, duration: 150, type: 'square' },
    ],
    repeatDelay: 300,
    loops: 3,
  },
  'alarm-high': {
    name: 'High Alert',
    description: 'Warning tone for high glucose',
    patterns: [
      { frequency: 659, duration: 200, type: 'sine', ramp: { to: 880, duration: 200 } },
      { frequency: 0, duration: 100, type: 'sine' },
      { frequency: 659, duration: 200, type: 'sine', ramp: { to: 880, duration: 200 } },
      { frequency: 0, duration: 100, type: 'sine' },
      { frequency: 880, duration: 400, type: 'sine' },
    ],
    repeatDelay: 500,
    loops: 2,
  },
  'alarm-low': {
    name: 'Low Alert',
    description: 'Warning tone for low glucose',
    patterns: [
      { frequency: 440, duration: 300, type: 'sine', ramp: { to: 330, duration: 300 } },
      { frequency: 0, duration: 150, type: 'sine' },
      { frequency: 440, duration: 300, type: 'sine', ramp: { to: 330, duration: 300 } },
      { frequency: 0, duration: 150, type: 'sine' },
      { frequency: 330, duration: 500, type: 'sine' },
    ],
    repeatDelay: 400,
    loops: 2,
  },
  'alarm-default': {
    name: 'Default Alarm',
    description: 'Standard alarm sound',
    patterns: [
      { frequency: 523, duration: 200, type: 'sine' },
      { frequency: 0, duration: 100, type: 'sine' },
      { frequency: 659, duration: 200, type: 'sine' },
      { frequency: 0, duration: 100, type: 'sine' },
      { frequency: 784, duration: 300, type: 'sine' },
    ],
    repeatDelay: 400,
    loops: 2,
  },
  'alert': {
    name: 'Alert',
    description: 'General alert sound',
    patterns: [
      { frequency: 587, duration: 150, type: 'triangle' },
      { frequency: 0, duration: 75, type: 'sine' },
      { frequency: 698, duration: 150, type: 'triangle' },
      { frequency: 0, duration: 75, type: 'sine' },
      { frequency: 587, duration: 150, type: 'triangle' },
    ],
    repeatDelay: 300,
    loops: 2,
  },
  'chime': {
    name: 'Chime',
    description: 'Pleasant chime',
    patterns: [
      { frequency: 1047, duration: 100, type: 'sine' },
      { frequency: 1319, duration: 100, type: 'sine' },
      { frequency: 1568, duration: 200, type: 'sine' },
    ],
    repeatDelay: 200,
    loops: 1,
  },
  'bell': {
    name: 'Bell',
    description: 'Bell ring',
    patterns: [
      { frequency: 830, duration: 600, type: 'sine' },
    ],
    repeatDelay: 400,
    loops: 2,
  },
  'siren': {
    name: 'Siren',
    description: 'Emergency siren',
    patterns: [
      { frequency: 600, duration: 500, type: 'sawtooth', ramp: { to: 1200, duration: 500 } },
      { frequency: 1200, duration: 500, type: 'sawtooth', ramp: { to: 600, duration: 500 } },
    ],
    repeatDelay: 0,
    loops: 3,
  },
  'beep': {
    name: 'Beep',
    description: 'Simple beep',
    patterns: [
      { frequency: 800, duration: 200, type: 'square' },
    ],
    repeatDelay: 300,
    loops: 3,
  },
  'soft': {
    name: 'Soft',
    description: 'Gentle, quiet notification',
    patterns: [
      { frequency: 392, duration: 300, type: 'sine' },
      { frequency: 0, duration: 100, type: 'sine' },
      { frequency: 494, duration: 400, type: 'sine' },
    ],
    repeatDelay: 200,
    loops: 1,
  },
};

// Vibration patterns for different alarm types
const VIBRATION_PATTERNS: Record<string, number[]> = {
  'alarm-urgent': [200, 100, 200, 100, 400, 200, 200, 100, 200, 100, 400],
  'alarm-high': [300, 150, 300, 150, 300],
  'alarm-low': [400, 200, 400, 200, 600],
  'alarm-default': [200, 100, 200, 100, 200],
  'alert': [150, 75, 150, 75, 150],
  'chime': [100, 50, 100],
  'bell': [500, 200, 500],
  'siren': [100, 50, 100, 50, 100, 50, 100, 50, 100],
  'beep': [200],
  'soft': [100, 100, 150],
};

// Audio context singleton
let audioContext: AudioContext | null = null;
let currentOscillator: OscillatorNode | null = null;
let currentGainNode: GainNode | null = null;
let isPlaying = false;
let stopRequested = false;
let wakeLock: WakeLockSentinel | null = null;
let activeNotification: Notification | null = null;

function getAudioContext(): AudioContext {
  if (!audioContext) {
    audioContext = new AudioContext();
  }
  return audioContext;
}

// ============================================================================
// Native Browser API Helpers
// ============================================================================

/**
 * Check if the Notifications API is available and permission is granted
 */
export function canShowNotifications(): boolean {
  return 'Notification' in window && Notification.permission === 'granted';
}

/**
 * Request permission to show notifications
 */
export async function requestNotificationPermission(): Promise<NotificationPermission> {
  if (!('Notification' in window)) {
    return 'denied';
  }
  return await Notification.requestPermission();
}

/**
 * Get current notification permission status
 */
export function getNotificationPermission(): NotificationPermission | 'unsupported' {
  if (!('Notification' in window)) {
    return 'unsupported';
  }
  return Notification.permission;
}

/**
 * Check if the Vibration API is available
 */
export function canVibrate(): boolean {
  return 'vibrate' in navigator;
}

/**
 * Trigger device vibration with a pattern
 * @param pattern Array of [vibrate, pause, vibrate, ...] durations in ms
 */
export function triggerVibration(pattern: number[] = [200, 100, 200]): boolean {
  if (!canVibrate()) {
    return false;
  }
  return navigator.vibrate(pattern);
}

/**
 * Stop any ongoing vibration
 */
export function stopVibration(): void {
  if (canVibrate()) {
    navigator.vibrate(0);
  }
}

/**
 * Check if Screen Wake Lock API is available
 */
export function canPreventSleep(): boolean {
  return 'wakeLock' in navigator;
}

/**
 * Request a wake lock to prevent the screen from sleeping
 */
async function requestWakeLock(): Promise<void> {
  if (!canPreventSleep()) return;

  try {
    wakeLock = await navigator.wakeLock.request('screen');
    wakeLock.addEventListener('release', () => {
      wakeLock = null;
    });
  } catch (err) {
    // Wake lock request failed (e.g., low battery, or document not visible)
    console.debug('Wake lock request failed:', err);
  }
}

/**
 * Release the wake lock
 */
async function releaseWakeLock(): Promise<void> {
  if (wakeLock) {
    try {
      await wakeLock.release();
    } catch {
      // Ignore errors
    }
    wakeLock = null;
  }
}

/**
 * Show a system notification
 */
function showNotification(title: string, body: string, tag: string = 'alarm'): Notification | null {
  if (!canShowNotifications()) {
    return null;
  }

  // Close any existing alarm notification
  if (activeNotification) {
    activeNotification.close();
  }

  activeNotification = new Notification(title, {
    body,
    tag, // Replaces existing notifications with same tag
    icon: '/images/logo-128.png', // Nocturne app icon
    badge: '/images/logo-64.png',
    requireInteraction: true, // Notification stays until user interacts
    silent: true, // We're handling our own sound
  });

  activeNotification.onclick = () => {
    window.focus();
    activeNotification?.close();
  };

  return activeNotification;
}

/**
 * Close any active notification
 */
function closeNotification(): void {
  if (activeNotification) {
    activeNotification.close();
    activeNotification = null;
  }
}

/**
 * Play a single tone
 */
async function playTone(
  ctx: AudioContext,
  gainNode: GainNode,
  pattern: TonePattern,
  _volume: number
): Promise<void> {
  return new Promise((resolve) => {
    if (stopRequested) {
      resolve();
      return;
    }

    if (pattern.frequency === 0) {
      // Silence
      setTimeout(resolve, pattern.duration);
      return;
    }

    const oscillator = ctx.createOscillator();
    currentOscillator = oscillator;

    oscillator.type = pattern.type;
    oscillator.frequency.setValueAtTime(pattern.frequency, ctx.currentTime);

    if (pattern.ramp) {
      oscillator.frequency.linearRampToValueAtTime(
        pattern.ramp.to,
        ctx.currentTime + pattern.ramp.duration / 1000
      );
    }

    oscillator.connect(gainNode);
    oscillator.start();

    setTimeout(() => {
      oscillator.stop();
      oscillator.disconnect();
      currentOscillator = null;
      resolve();
    }, pattern.duration);
  });
}

/**
 * Play an alarm sound by ID with native browser API integration.
 * Supports both built-in synthesized sounds and custom uploaded sounds.
 */
export async function playAlarmSound(
  soundId: string,
  options: AlarmSoundOptions = {}
): Promise<void> {
  // Check if this is a custom sound
  if (isCustomSound(soundId)) {
    return playCustomSound(soundId, options);
  }

  const definition = ALARM_DEFINITIONS[soundId];
  if (!definition) {
    console.warn(`Unknown alarm sound: ${soundId}`);
    return;
  }

  // Stop any currently playing sound
  stopAlarmSound();

  stopRequested = false;
  isPlaying = true;

  // Request wake lock to prevent screen sleep during alarm
  if (options.preventSleep !== false) {
    await requestWakeLock();
  }

  // Show system notification if requested
  if (options.showNotification) {
    showNotification(
      options.notificationTitle ?? definition.name,
      options.notificationBody ?? definition.description,
      `alarm-${soundId}`
    );
  }

  // Trigger vibration if requested and supported
  if (options.vibrate !== false && canVibrate()) {
    const vibrationPattern = options.vibrationPattern ?? VIBRATION_PATTERNS[soundId] ?? [200, 100, 200];
    triggerVibration(vibrationPattern);
  }

  const ctx = getAudioContext();

  // Resume context if suspended (browser autoplay policy)
  if (ctx.state === 'suspended') {
    await ctx.resume();
  }

  const gainNode = ctx.createGain();
  currentGainNode = gainNode;
  gainNode.connect(ctx.destination);

  const baseVolume = (options.volume ?? 80) / 100;
  const startVolume = options.ascending ? (options.startVolume ?? 20) / 100 : baseVolume;
  const ascendDuration = (options.ascendDurationSeconds ?? 30) * 1000;

  gainNode.gain.setValueAtTime(startVolume * 0.3, ctx.currentTime); // Scale down to prevent clipping

  if (options.ascending) {
    gainNode.gain.linearRampToValueAtTime(
      baseVolume * 0.3,
      ctx.currentTime + ascendDuration / 1000
    );
  }

  try {
  // Calculate natural duration of one loop
  let loopDuration = 0;
  for (const pattern of definition.patterns) {
    loopDuration += pattern.duration;
  }
  loopDuration += definition.repeatDelay;

  // Determine number of loops
  let totalLoops = definition.loops;
  if (options.minDurationSeconds) {
    const minDurationMs = options.minDurationSeconds * 1000;
    const requiredLoops = Math.ceil(minDurationMs / loopDuration);
    totalLoops = Math.max(totalLoops, requiredLoops);
  }

    for (let loop = 0; loop < totalLoops; loop++) {
      if (stopRequested) break;

      for (const pattern of definition.patterns) {
        if (stopRequested) break;
        await playTone(ctx, gainNode, pattern, baseVolume);
      }

      if (loop < totalLoops - 1 && definition.repeatDelay > 0) {
        await new Promise(resolve => setTimeout(resolve, definition.repeatDelay));
      }
    }
  } finally {
    gainNode.disconnect();
    currentGainNode = null;
    isPlaying = false;
    stopRequested = false;

    // Clean up native API resources
    await releaseWakeLock();
    stopVibration();
    // Note: We don't close notification here - let user dismiss it
  }
}

/**
 * Stop the currently playing alarm sound and clean up all native API resources
 */
export function stopAlarmSound(): void {
  stopRequested = true;

  // Stop Web Audio API oscillator
  if (currentOscillator) {
    try {
      currentOscillator.stop();
      currentOscillator.disconnect();
    } catch {
      // Ignore errors if already stopped
    }
    currentOscillator = null;
  }

  if (currentGainNode) {
    try {
      currentGainNode.disconnect();
    } catch {
      // Ignore errors
    }
    currentGainNode = null;
  }

  // Stop custom audio element
  if (currentAudioElement) {
    try {
      currentAudioElement.pause();
      currentAudioElement.src = '';
    } catch {
      // Ignore errors
    }
    currentAudioElement = null;
  }

  // Stop native API resources
  stopVibration();
  closeNotification();
  releaseWakeLock();

  isPlaying = false;
}

/**
 * Check if an alarm is currently playing
 */
export function isAlarmPlaying(): boolean {
  return isPlaying;
}

/**
 * Get all available built-in alarm sound definitions
 */
export function getAlarmDefinitions(): Record<string, { name: string; description: string }> {
  const result: Record<string, { name: string; description: string }> = {};
  for (const [id, def] of Object.entries(ALARM_DEFINITIONS)) {
    result[id] = { name: def.name, description: def.description };
  }
  return result;
}

/**
 * Get all available alarm sounds (built-in + custom)
 */
export async function getAllAlarmSounds(): Promise<Array<{ id: string; name: string; description: string; isCustom: boolean }>> {
  const result: Array<{ id: string; name: string; description: string; isCustom: boolean }> = [];

  // Add built-in sounds
  for (const [id, def] of Object.entries(ALARM_DEFINITIONS)) {
    result.push({
      id,
      name: def.name,
      description: def.description,
      isCustom: false,
    });
  }

  // Add custom sounds
  const customSounds = await loadCustomSounds();
  for (const sound of customSounds) {
    result.push({
      id: sound.id,
      name: sound.name,
      description: `Custom: ${sound.fileName}`,
      isCustom: true,
    });
  }

  return result;
}

/**
 * Preview an alarm with visual feedback
 */
export interface PreviewState {
  isPlaying: boolean;
  soundId: string | null;
}

let previewStateCallbacks: ((state: PreviewState) => void)[] = [];

export function subscribeToPreviewState(callback: (state: PreviewState) => void): () => void {
  previewStateCallbacks.push(callback);
  return () => {
    previewStateCallbacks = previewStateCallbacks.filter(cb => cb !== callback);
  };
}

function notifyPreviewState(state: PreviewState): void {
  previewStateCallbacks.forEach(cb => cb(state));
}

export async function previewAlarmSound(
  soundId: string,
  options: AlarmSoundOptions = {}
): Promise<void> {
  notifyPreviewState({ isPlaying: true, soundId });

  try {
    // For preview, disable notifications and wake lock by default
    await playAlarmSound(soundId, {
      ...options,
      showNotification: options.showNotification ?? false,
      preventSleep: options.preventSleep ?? false,
    });
  } finally {
    notifyPreviewState({ isPlaying: false, soundId: null });
  }
}

export function stopPreview(): void {
  stopAlarmSound();
  notifyPreviewState({ isPlaying: false, soundId: null });
}

// ============================================================================
// Browser Capabilities
// ============================================================================

export interface BrowserAlarmCapabilities {
  audio: boolean;
  notifications: boolean;
  notificationPermission: NotificationPermission | 'unsupported';
  vibration: boolean;
  wakeLock: boolean;
}

/**
 * Get the current browser's alarm-related capabilities
 */
export function getBrowserCapabilities(): BrowserAlarmCapabilities {
  return {
    audio: typeof AudioContext !== 'undefined' || typeof (window as unknown as { webkitAudioContext: unknown }).webkitAudioContext !== 'undefined',
    notifications: 'Notification' in window,
    notificationPermission: getNotificationPermission(),
    vibration: canVibrate(),
    wakeLock: canPreventSleep(),
  };
}

/**
 * Get the vibration pattern for a specific alarm sound
 */
export function getVibrationPattern(soundId: string): number[] {
  return VIBRATION_PATTERNS[soundId] ?? [200, 100, 200];
}

// ============================================================================
// Custom Sound Upload & Management
// ============================================================================

const CUSTOM_SOUNDS_STORAGE_KEY = 'nocturne-custom-alarm-sounds';
const MAX_CUSTOM_SOUND_SIZE_MB = 5;
const ALLOWED_AUDIO_TYPES = ['audio/mpeg', 'audio/wav', 'audio/ogg', 'audio/webm', 'audio/mp4', 'audio/aac'];

export interface CustomAlarmSound {
  id: string;
  name: string;
  fileName: string;
  mimeType: string;
  size: number;
  /** Base64 encoded audio data */
  dataUrl: string;
  createdAt: string;
}

/** Storage for custom sounds - uses IndexedDB via a simple wrapper */
const customSoundsCache: Map<string, CustomAlarmSound> = new Map();
let customSoundsLoaded = false;

/**
 * Load custom sounds from localStorage/IndexedDB
 */
export async function loadCustomSounds(): Promise<CustomAlarmSound[]> {
  if (customSoundsLoaded) {
    return Array.from(customSoundsCache.values());
  }

  try {
    // Try IndexedDB first for larger storage
    const sounds = await loadFromIndexedDB();
    if (sounds) {
      sounds.forEach(s => customSoundsCache.set(s.id, s));
      customSoundsLoaded = true;
      return sounds;
    }
  } catch {
    // Fall back to localStorage
  }

  try {
    const stored = localStorage.getItem(CUSTOM_SOUNDS_STORAGE_KEY);
    if (stored) {
      const sounds: CustomAlarmSound[] = JSON.parse(stored);
      sounds.forEach(s => customSoundsCache.set(s.id, s));
      customSoundsLoaded = true;
      return sounds;
    }
  } catch (err) {
    console.error('Failed to load custom sounds:', err);
  }

  customSoundsLoaded = true;
  return [];
}

/**
 * Save custom sounds to storage
 */
async function saveCustomSounds(): Promise<void> {
  const sounds = Array.from(customSoundsCache.values());

  try {
    // Try IndexedDB first
    await saveToIndexedDB(sounds);
  } catch {
    // Fall back to localStorage (may fail for large files)
    try {
      localStorage.setItem(CUSTOM_SOUNDS_STORAGE_KEY, JSON.stringify(sounds));
    } catch (err) {
      console.error('Failed to save custom sounds:', err);
      throw new Error('Storage quota exceeded. Try removing some custom sounds.', { cause: err });
    }
  }
}

// IndexedDB helpers
const DB_NAME = 'nocturne-audio';
const DB_VERSION = 1;
const STORE_NAME = 'custom-sounds';

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);

    request.onerror = () => reject(request.error);
    request.onsuccess = () => resolve(request.result);

    request.onupgradeneeded = (event) => {
      const db = (event.target as IDBOpenDBRequest).result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME, { keyPath: 'id' });
      }
    };
  });
}

async function loadFromIndexedDB(): Promise<CustomAlarmSound[] | null> {
  try {
    const db = await openDatabase();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(STORE_NAME, 'readonly');
      const store = transaction.objectStore(STORE_NAME);
      const request = store.getAll();

      request.onerror = () => reject(request.error);
      request.onsuccess = () => resolve(request.result);
    });
  } catch {
    return null;
  }
}

async function saveToIndexedDB(sounds: CustomAlarmSound[]): Promise<void> {
  const db = await openDatabase();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readwrite');
    const store = transaction.objectStore(STORE_NAME);

    // Clear existing and add all
    store.clear();
    sounds.forEach(sound => store.put(sound));

    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
  });
}

/**
 * Upload a custom alarm sound from a File
 */
export async function uploadCustomSound(file: File, name?: string): Promise<CustomAlarmSound> {
  // Validate file type
  if (!ALLOWED_AUDIO_TYPES.includes(file.type)) {
    throw new Error(`Invalid file type: ${file.type}. Allowed types: MP3, WAV, OGG, WebM, M4A, AAC`);
  }

  // Validate file size
  const sizeMB = file.size / (1024 * 1024);
  if (sizeMB > MAX_CUSTOM_SOUND_SIZE_MB) {
    throw new Error(`File too large: ${sizeMB.toFixed(1)}MB. Maximum size: ${MAX_CUSTOM_SOUND_SIZE_MB}MB`);
  }

  // Read file as data URL
  const dataUrl = await readFileAsDataUrl(file);

  // Validate that the audio can be decoded
  await validateAudioFile(dataUrl);

  const sound: CustomAlarmSound = {
    id: `custom-${randomUUID()}`,
    name: name || file.name.replace(/\.[^.]+$/, ''), // Remove extension for display name
    fileName: file.name,
    mimeType: file.type,
    size: file.size,
    dataUrl,
    createdAt: new Date().toISOString(),
  };

  // Ensure sounds are loaded
  await loadCustomSounds();

  customSoundsCache.set(sound.id, sound);
  await saveCustomSounds();

  return sound;
}

/**
 * Delete a custom alarm sound
 */
export async function deleteCustomSound(soundId: string): Promise<boolean> {
  await loadCustomSounds();

  if (!customSoundsCache.has(soundId)) {
    return false;
  }

  customSoundsCache.delete(soundId);
  await saveCustomSounds();
  return true;
}

/**
 * Get a custom sound by ID
 */
export async function getCustomSound(soundId: string): Promise<CustomAlarmSound | null> {
  await loadCustomSounds();
  return customSoundsCache.get(soundId) ?? null;
}

/**
 * Check if a sound ID is a custom sound
 */
export function isCustomSound(soundId: string): boolean {
  return soundId.startsWith('custom-');
}

/**
 * Get all custom sounds
 */
export async function getCustomSounds(): Promise<CustomAlarmSound[]> {
  return loadCustomSounds();
}

/**
 * Read a File as a data URL
 */
function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

/**
 * Validate that an audio file can be decoded
 */
async function validateAudioFile(dataUrl: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const audio = new Audio();

    audio.oncanplaythrough = () => {
      audio.src = ''; // Clean up
      resolve();
    };

    audio.onerror = () => {
      reject(new Error('Could not decode audio file. Please ensure it is a valid audio file.'));
    };

    // Set a timeout in case the file never loads
    const timeout = setTimeout(() => {
      audio.src = '';
      reject(new Error('Audio file validation timed out.'));
    }, 10000);

    audio.oncanplaythrough = () => {
      clearTimeout(timeout);
      audio.src = '';
      resolve();
    };

    audio.src = dataUrl;
    audio.load();
  });
}

// ============================================================================
// Custom Sound Playback
// ============================================================================

let currentAudioElement: HTMLAudioElement | null = null;

/**
 * Play a custom sound by its ID or data URL
 */
export async function playCustomSound(
  soundIdOrUrl: string,
  options: AlarmSoundOptions = {}
): Promise<void> {
  // Stop any currently playing sound
  stopAlarmSound();

  stopRequested = false;
  isPlaying = true;

  // Get the data URL
  let dataUrl: string;
  if (soundIdOrUrl.startsWith('data:')) {
    dataUrl = soundIdOrUrl;
  } else {
    const sound = await getCustomSound(soundIdOrUrl);
    if (!sound) {
      throw new Error(`Custom sound not found: ${soundIdOrUrl}`);
    }
    dataUrl = sound.dataUrl;
  }

  // Request wake lock
  if (options.preventSleep !== false) {
    await requestWakeLock();
  }

  // Show notification if requested
  if (options.showNotification) {
    showNotification(
      options.notificationTitle ?? 'Alarm',
      options.notificationBody ?? 'Custom alarm sound',
      'alarm-custom'
    );
  }

  // Trigger vibration
  if (options.vibrate !== false && canVibrate()) {
    const vibrationPattern = options.vibrationPattern ?? [200, 100, 200];
    triggerVibration(vibrationPattern);
  }

  // Create and configure audio element
  const audio = new Audio(dataUrl);
  currentAudioElement = audio;

  const baseVolume = (options.volume ?? 80) / 100;
  const startVolume = options.ascending ? (options.startVolume ?? 20) / 100 : baseVolume;

  audio.volume = startVolume;

  // Handle ascending volume
  if (options.ascending) {
    const ascendDuration = (options.ascendDurationSeconds ?? 30) * 1000;
    const volumeStep = (baseVolume - startVolume) / (ascendDuration / 100);

    const volumeInterval = setInterval(() => {
      if (stopRequested || !currentAudioElement) {
        clearInterval(volumeInterval);
        return;
      }

      if (audio.volume < baseVolume) {
        audio.volume = Math.min(baseVolume, audio.volume + volumeStep);
      } else {
        clearInterval(volumeInterval);
      }
    }, 100);
  }

  // Play the audio
  try {
    await audio.play();

    // Wait for it to finish or be stopped
    await new Promise<void>((resolve) => {
      audio.onended = () => resolve();
      audio.onerror = () => resolve();

      // Check periodically if stop was requested
      const checkStop = setInterval(() => {
        if (stopRequested) {
          clearInterval(checkStop);
          resolve();
        }
      }, 100);
    });
  } finally {
    if (currentAudioElement === audio) {
      audio.pause();
      audio.src = '';
      currentAudioElement = null;
    }

    isPlaying = false;
    stopRequested = false;

    await releaseWakeLock();
    stopVibration();
  }
}
