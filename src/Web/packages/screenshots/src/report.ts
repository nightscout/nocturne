/** Prints a check's findings and leaves a failing exit status behind when there are any. */
export function report(heading: string, problems: string[], clean: string): void {
	if (problems.length > 0) {
		console.error(`${heading}:\n  ${problems.join('\n  ')}`);
		process.exit(1);
	}
	console.log(clean);
}
