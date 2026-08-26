import { findBrokenReferences } from './references.js';

const broken = await findBrokenReferences();
if (broken.length > 0) {
	console.error(`Broken screenshot references:\n  ${broken.join('\n  ')}`);
	process.exit(1);
}
console.log('All screenshot references resolve.');
