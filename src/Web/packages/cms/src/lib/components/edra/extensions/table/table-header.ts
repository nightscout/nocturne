import { TableHeader as TiptapTableHeader } from '@tiptap/extension-table';
import { Plugin } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';
import strings from '../../strings.ts';

import { getCellsInRow, isColumnSelected, selectColumn } from './utils.ts';

const showGripAt = (target: EventTarget | null) => {
	if (!(target instanceof Element)) return false;
	const cell = target.closest('td, th');
	const table = target.closest('table');
	if (!cell || !table) return false;
	const index = cell instanceof HTMLTableCellElement ? cell.cellIndex : undefined;
	const grips = table.querySelectorAll<HTMLAnchorElement>('a.grip-column');
	grips.forEach((g, idx) => {
		if (idx === index) g.classList.add('show-col-grip');
		else g.classList.remove('show-col-grip');
	});
	const wrapper = table.closest('.tableWrapper');
	if (wrapper) {
		const lastIndex = table.rows[0]?.cells.length ? table.rows[0].cells.length - 1 : -1;
		if (index === lastIndex) wrapper.classList.add('last-column-hover');
		else wrapper.classList.remove('last-column-hover');
	}
	return false;
};

const hideGrips = (table: HTMLTableElement | null) => {
	if (!table) return;
	const grips = table.querySelectorAll<HTMLAnchorElement>('a.grip-column');
	grips.forEach((g) => g.classList.remove('show-col-grip'));
	const wrapper = table.closest('.tableWrapper');
	if (wrapper) wrapper.classList.remove('last-column-hover');
};

export const TableHeader = TiptapTableHeader.extend({
	addAttributes() {
		return {
			colspan: {
				default: 1
			},
			rowspan: {
				default: 1
			},
			colwidth: {
				default: null,
				parseHTML: (element) => {
					const colwidth = element.getAttribute('colwidth');
					const value = colwidth
						? colwidth.split(',').map((item) => Number.parseInt(item, 10))
						: null;

					return value;
				}
			},
			style: {
				default: null
			}
		};
	},

	addProseMirrorPlugins() {
		const { isEditable } = this.editor;

		return [
			new Plugin({
				props: {
					decorations: (state) => {
						if (!isEditable) {
							return DecorationSet.empty;
						}

						const { doc, selection } = state;
						const decorations: Decoration[] = [];
						const cells = getCellsInRow(0)(selection);

						if (cells) {
							cells.forEach(({ pos }: { pos: number }, index: number) => {
								decorations.push(
									Decoration.widget(pos + 1, () => {
										const colSelected = isColumnSelected(index)(selection);
										let className = 'grip-column';

										if (colSelected) {
											className += ' selected';
										}

										if (index === 0) {
											className += ' first';
										}

										if (index === cells.length - 1) {
											className += ' last';
										}

										const grip = document.createElement('a');

										grip.className = className;
										grip.setAttribute('role', 'button');
										grip.setAttribute('aria-label', strings.extension.table.selectColumn);
										grip.setAttribute('tabindex', '0');
										grip.dataset.colIndex = String(index);
										grip.addEventListener('mousedown', (event) => {
											event.preventDefault();
											event.stopImmediatePropagation();

											this.editor.view.dispatch(selectColumn(index)(this.editor.state.tr));
										});

										return grip;
									})
								);
							});

							// Add-column "+" button — anchored to the last column of the header row
							const lastHeaderCell = cells[cells.length - 1];
							decorations.push(
								Decoration.widget(lastHeaderCell.pos + 1, () => {
									const btn = document.createElement('button');
									btn.className = 'add-column-btn';
									btn.type = 'button';
									btn.setAttribute('aria-label', strings.extension.table.addColumn);
									btn.setAttribute('title', strings.extension.table.addColumnAfter);
									btn.textContent = '+';
									btn.addEventListener('mousedown', (event) => {
										event.preventDefault();
										event.stopImmediatePropagation();
										// Select last column, then add after
										this.editor.view.dispatch(selectColumn(cells.length - 1)(this.editor.state.tr));
										this.editor.chain().focus().addColumnAfter().run();
									});

									return btn;
								})
							);
						}

						return DecorationSet.create(doc, decorations);
					}
				}
			}),
			// Interaction plugin to toggle visibility of column grips based on hover/click
			new Plugin({
				props: {
					handleDOMEvents: {
						mousemove: (view, event) => showGripAt(event.target),
						focusin: (view, event) => showGripAt(event.target),
						mousedown: (view, event) => showGripAt(event.target),
						mouseleave: (view, event) => {
							if (event.target instanceof Element) hideGrips(event.target.closest('table'));
							return false;
						},
						mouseout: (view, event) => {
							if (!(event.target instanceof Element)) return false;
							const table = event.target.closest('table');
							const to = event.relatedTarget;
							const toTable = to instanceof Element ? to.closest('table') : null;
							if (table && toTable !== table) hideGrips(table);
							return false;
						},
						touchstart: (view, event) => showGripAt(event.target),
						touchmove: (view, event) => showGripAt(event.target)
					}
				}
			})
		];
	}
});

export default TableHeader;
