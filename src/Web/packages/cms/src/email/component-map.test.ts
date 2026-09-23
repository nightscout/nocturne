import { describe, it, expect } from 'vitest';
import type { Component } from 'svelte';
import { validateComponentUsage } from './component-map.ts';

const Stub: Component = () => ({});

describe('validateComponentUsage', () => {
	it('returns empty array when all components are in the allowlist', () => {
		const result = validateComponentUsage(['Callout', 'Var'], {
			Callout: Stub,
			Var: Stub,
		});
		expect(result).toEqual([]);
	});

	it('returns missing components', () => {
		const result = validateComponentUsage(['Callout', 'Button', 'Chart'], {
			Callout: Stub,
		});
		expect(result).toEqual(['Button', 'Chart']);
	});

	it('handles empty usage list', () => {
		const result = validateComponentUsage([], { Callout: Stub });
		expect(result).toEqual([]);
	});

	it('handles empty allowlist', () => {
		const result = validateComponentUsage(['Callout'], {});
		expect(result).toEqual(['Callout']);
	});
});
