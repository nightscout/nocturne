/**
 * Hand-written mirror of the gitignored `src/wasm/nocturne_watercolour.d.ts`,
 * so a clone without `build:wasm` still type-checks.
 */

export interface EngineStats {
  liveInstances: number;
  maxLiveInstances: number;
  checkpointBytes: number;
  lastStepMs: number;
  lastRenderMs: number;
  initMs: number;
  adapterName: string;
}

export interface WasmInstance {
  free(): void;
  attach(canvas: HTMLCanvasElement, width: number, height: number): void;
  resize(width: number, height: number): void;
  play(): void;
  pause(): void;
  reset(): void;
  advanceByElapsed(seconds: number): void;
  /** Linear progress-to-tick drive; callers apply their own easing first. */
  advanceToProgress(progress: number): void;
  setProgressCurve(curve: 'frontLoaded' | 'linear' | 'reveal'): void;
  seekProgress(progress: number): void;
  finishImmediately(): void;
  progress(): number;
  isFinished(): boolean;
  isPlaying(): boolean;
  render(): boolean;
  exportPng(width: number, height: number): Promise<Uint8Array>;
  exportFrames(count: number, width: number, height: number): Promise<Uint8Array[]>;
  exportStrip(count: number, size: number): Promise<Uint8Array>;
  /** Pixel size the swapchain is configured at; `undefined` until `attach`. */
  surfaceSize(): number[] | undefined;
  /** Simulation grid side the loaded scene runs at. */
  simResolution(): number;
  /** Simulation steps the loaded scene's timeline runs. */
  totalTicks(): number;
  checkpointBytes(): number;
  detach(): void;
  dispose(): void;
}

export interface WasmEngine {
  free(): void;
  /**
   * `settleFraction` (0 = unchanged) lengthens the reveal's drying tail.
   * `paintWallFraction` (0 = keep the default) is the share of the wall clock
   * the brushwork gets; the playback runs `ProgressCurve::reveal_for(scene,
   * paintWallFraction)` so the tail covers the settling after the pen leaves
   * the paper. `checkpointBudgetBytes` (absent or 0 = the engine default) is
   * this instance's own seek-checkpoint budget; below one checkpoint it still
   * keeps the one at tick 0, so `seekProgress` replays from the start.
   */
  createInstance(sceneJson: string, durationMs: number, settleFraction?: number, paintWallFraction?: number, checkpointBudgetBytes?: number): WasmInstance;
  isLost(): boolean;
  onDeviceLost(callback: (message: string) => void): void;
  adapterName(): string;
  stats(): EngineStats;
  maxLiveInstances: number;
}

export interface WasmModule {
  default(moduleOrPath?: unknown): Promise<unknown>;
  WatercolourEngine: { create(): Promise<WasmEngine> };
  catalogueScene(artworkId: string, seed: number, palette: string, intensity: number, detail: string, surface: string, simResolution: number): string;
  /** Authors a watercolour scene from a Lucide element list (JSON) and returns its scene document. `hintsJson` is per-icon tuning (`""` keeps the defaults). */
  iconScene(elementsJson: string, name: string, seed: number, palette: string, intensity: number, detail: string, surface: string, simResolution: number, hintsJson: string): string;
  catalogueIds(): string[];
  bakedManifest(frames: number, width: number, height: number, durationMs: number): string;
}
