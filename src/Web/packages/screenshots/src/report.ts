import { findBrokenEmbeds } from './embeds.js';
import { findBrokenReferences } from './references.js';

interface Check {
	heading: string;
	clean: string;
	find: () => Promise<string[]>;
}

/** Named once, so a capture run and the standalone script report the same finding identically. */
export const references: Check = {
	heading: 'Broken screenshot references',
	clean: 'All screenshot references resolve.',
	find: findBrokenReferences,
};

export const embeds: Check = {
	heading: 'Broken screenshot embeds',
	clean: 'All documentation screenshot embeds resolve.',
	find: findBrokenEmbeds,
};

/**
 * Prints every check's findings and leaves a failing exit status behind when any has them. Each
 * check runs even after an earlier one failed: a docs change usually breaks both at once, and
 * stopping at the first would hide half the work.
 */
export async function report(...checks: Check[]): Promise<void> {
	let failed = false;

	for (const check of checks) {
		const problems = await check.find();
		if (problems.length === 0) {
			console.log(check.clean);
			continue;
		}
		console.error(`${check.heading}:\n  ${problems.join('\n  ')}`);
		failed = true;
	}

	if (failed) process.exit(1);
}
