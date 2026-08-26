import type { Page } from '@playwright/test';

export type Theme = 'light' | 'dark';
export type Viewport = 'desktop' | 'mobile';
export type Scenario = 'patient' | 'first-run';

/** A screenshot to capture, declared in the manifest source. */
export interface ScreenshotDefinition {
	/** Kebab-case identifier; becomes the image filename stem and the id docs embed. */
	id: string;
	/** App route relative to the tenant origin. */
	route: string;
	/** Plain-language description of what the reader sees; rendered as the image alt text. */
	alt: string;
	scenario?: Scenario;
	viewport?: Viewport;
	/** Selector to crop the capture to; the viewport when absent. */
	clip?: string;
	/** Capture the whole document height instead of the viewport. Ignored when clip is set. */
	fullPage?: boolean;
	/** Named callout targets; bounding boxes are recorded into manifest.json at capture time. */
	anchors?: Record<string, string>;
	/** Runs after navigation, before capture (open a dialog, hover a chart). */
	prepare?: (page: Page) => Promise<void>;
}

export interface ManifestVariant {
	file: string;
	width: number;
	height: number;
}

export interface ManifestAnchor {
	x: number;
	y: number;
	width: number;
	height: number;
}

export interface ManifestEntry {
	alt: string;
	variants: Record<Theme, ManifestVariant>;
	anchors?: Record<string, ManifestAnchor>;
}

/** Shape of the generated manifest.json, keyed by screenshot id. */
export type Manifest = Record<string, ManifestEntry>;
