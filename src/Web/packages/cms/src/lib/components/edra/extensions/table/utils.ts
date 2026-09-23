import { type Editor, findParentNode } from '@tiptap/core';
import type { Node, ResolvedPos } from '@tiptap/pm/model';
import type { EditorState, Selection, Transaction } from '@tiptap/pm/state';
import { CellSelection, type Rect, TableMap } from '@tiptap/pm/tables';
import type { EditorView } from '@tiptap/pm/view';
import Table from './table.ts';

type CellInfo = { pos: number; start: number; node: Node | null | undefined };

export const isRectSelected = (rect: Rect) => (selection: CellSelection) => {
	const map = TableMap.get(selection.$anchorCell.node(-1));
	const start = selection.$anchorCell.start(-1);
	const cells = map.cellsInRect(rect);
	const selectedCells = map.cellsInRect(
		map.rectBetween(selection.$anchorCell.pos - start, selection.$headCell.pos - start)
	);

	for (let i = 0, count = cells.length; i < count; i += 1) {
		if (selectedCells.indexOf(cells[i]) === -1) {
			return false;
		}
	}

	return true;
};

export const findTable = (selection: Selection) =>
	findParentNode((node) => node.type.spec.tableRole && node.type.spec.tableRole === 'table')(
		selection
	);

export const isCellSelection = (selection: Selection): selection is CellSelection =>
	selection instanceof CellSelection;

export const isColumnSelected = (columnIndex: number) => (selection: Selection) => {
	if (isCellSelection(selection)) {
		const map = TableMap.get(selection.$anchorCell.node(-1));

		return isRectSelected({
			left: columnIndex,
			right: columnIndex + 1,
			top: 0,
			bottom: map.height
		})(selection);
	}

	return false;
};

export const isRowSelected = (rowIndex: number) => (selection: Selection) => {
	if (isCellSelection(selection)) {
		const map = TableMap.get(selection.$anchorCell.node(-1));

		return isRectSelected({
			left: 0,
			right: map.width,
			top: rowIndex,
			bottom: rowIndex + 1
		})(selection);
	}

	return false;
};

export const isTableSelected = (selection: Selection) => {
	if (isCellSelection(selection)) {
		const map = TableMap.get(selection.$anchorCell.node(-1));

		return isRectSelected({
			left: 0,
			right: map.width,
			top: 0,
			bottom: map.height
		})(selection);
	}

	return false;
};

export const getCellsInColumn = (columnIndex: number | number[]) => (selection: Selection) => {
	const table = findTable(selection);
	if (table) {
		const map = TableMap.get(table.node);
		const indexes = Array.isArray(columnIndex) ? columnIndex : Array.from([columnIndex]);

		return indexes.reduce<CellInfo[]>(
			(acc, index) => {
				if (index >= 0 && index <= map.width - 1) {
					const cells = map.cellsInRect({
						left: index,
						right: index + 1,
						top: 0,
						bottom: map.height
					});

					return acc.concat(
						cells.map((nodePos) => {
							const node = table.node.nodeAt(nodePos);
							const pos = nodePos + table.start;

							return { pos, start: pos + 1, node };
						})
					);
				}

				return acc;
			},
			[]
		);
	}
	return null;
};

export const getCellsInRow = (rowIndex: number | number[]) => (selection: Selection) => {
	const table = findTable(selection);

	if (table) {
		const map = TableMap.get(table.node);
		const indexes = Array.isArray(rowIndex) ? rowIndex : Array.from([rowIndex]);

		return indexes.reduce<CellInfo[]>(
			(acc, index) => {
				if (index >= 0 && index <= map.height - 1) {
					const cells = map.cellsInRect({
						left: 0,
						right: map.width,
						top: index,
						bottom: index + 1
					});

					return acc.concat(
						cells.map((nodePos) => {
							const node = table.node.nodeAt(nodePos);
							const pos = nodePos + table.start;
							return { pos, start: pos + 1, node };
						})
					);
				}

				return acc;
			},
			[]
		);
	}

	return null;
};

export const getCellsInTable = (selection: Selection) => {
	const table = findTable(selection);

	if (table) {
		const map = TableMap.get(table.node);
		const cells = map.cellsInRect({
			left: 0,
			right: map.width,
			top: 0,
			bottom: map.height
		});

		return cells.map((nodePos) => {
			const node = table.node.nodeAt(nodePos);
			const pos = nodePos + table.start;

			return { pos, start: pos + 1, node };
		});
	}

	return null;
};

export const findParentNodeClosestToPos = (
	$pos: ResolvedPos,
	predicate: (node: Node) => boolean
) => {
	for (let i = $pos.depth; i > 0; i -= 1) {
		const node = $pos.node(i);

		if (predicate(node)) {
			return {
				pos: i > 0 ? $pos.before(i) : 0,
				start: $pos.start(i),
				depth: i,
				node
			};
		}
	}

	return null;
};

export const findCellClosestToPos = ($pos: ResolvedPos) => {
	const predicate = (node: Node) =>
		node.type.spec.tableRole && /cell/i.test(node.type.spec.tableRole);

	return findParentNodeClosestToPos($pos, predicate);
};

const select = (type: 'row' | 'column') => (index: number) => (tr: Transaction) => {
	const table = findTable(tr.selection);
	const isRowSelection = type === 'row';

	if (table) {
		const map = TableMap.get(table.node);

		// Check if the index is valid
		if (index >= 0 && index < (isRowSelection ? map.height : map.width)) {
			const left = isRowSelection ? 0 : index;
			const top = isRowSelection ? index : 0;
			const right = isRowSelection ? map.width : index + 1;
			const bottom = isRowSelection ? index + 1 : map.height;

			const cellsInFirstRow = map.cellsInRect({
				left,
				top,
				right: isRowSelection ? right : left + 1,
				bottom: isRowSelection ? top + 1 : bottom
			});

			const cellsInLastRow =
				bottom - top === 1
					? cellsInFirstRow
					: map.cellsInRect({
							left: isRowSelection ? left : right - 1,
							top: isRowSelection ? bottom - 1 : top,
							right,
							bottom
						});

			const head = table.start + cellsInFirstRow[0];
			const anchor = table.start + cellsInLastRow[cellsInLastRow.length - 1];
			const $head = tr.doc.resolve(head);
			const $anchor = tr.doc.resolve(anchor);

			return tr.setSelection(new CellSelection($anchor, $head));
		}
	}
	return tr;
};

export const selectColumn = select('column');

export const selectRow = select('row');

export const selectTable = (tr: Transaction) => {
	const table = findTable(tr.selection);

	if (table) {
		const { map } = TableMap.get(table.node);

		if (map && map.length) {
			const head = table.start + map[0];
			const anchor = table.start + map[map.length - 1];
			const $head = tr.doc.resolve(head);
			const $anchor = tr.doc.resolve(anchor);

			return tr.setSelection(new CellSelection($anchor, $head));
		}
	}

	return tr;
};

const closestTableCell = (node: globalThis.Node | null | undefined): HTMLElement | null => {
	let current = node;
	while (current) {
		if (current instanceof HTMLElement && (current.tagName === 'TD' || current.tagName === 'TH')) {
			return current;
		}
		current = current.parentElement;
	}
	return null;
};

export const isColumnGripSelected = ({
	editor,
	view,
	state,
	from
}: {
	editor: Editor;
	view: EditorView;
	state: EditorState;
	from: number;
}) => {
	const node = view.nodeDOM(from) || view.domAtPos(from).node;

	if (!editor.isActive(Table.name) || !node || isTableSelected(state.selection)) {
		return false;
	}

	const container = closestTableCell(node);

	const gripColumn =
		container && container.querySelector && container.querySelector('a.grip-column.selected');

	return !!gripColumn;
};

export const isRowGripSelected = ({
	editor,
	view,
	state,
	from
}: {
	editor: Editor;
	view: EditorView;
	state: EditorState;
	from: number;
}) => {
	const node = view.nodeDOM(from) || view.domAtPos(from).node;

	if (!editor.isActive(Table.name) || !node || isTableSelected(state.selection)) {
		return false;
	}

	const container = closestTableCell(node);

	const gripRow =
		container && container.querySelector && container.querySelector('a.grip-row.selected');

	return !!gripRow;
};

// Show row menu when a cell in that row is selected (DOM-based)
export const isRowActiveFromSelection = ({
	editor,
	view,
	state,
	from
}: {
	editor: Editor;
	view: EditorView;
	state: EditorState;
	from: number;
}) => {
	if (!editor.isActive(Table.name)) return false;

	// Container at the BubbleMenu anchor
	const container = closestTableCell(view.nodeDOM(from) || view.domAtPos(from).node);
	if (!container) return false;

	const rowEl = container.closest('tr');
	if (!rowEl) return false;

	// Current selection anchor cell
	const anchorCell = closestTableCell(view.domAtPos(state.selection.$from.pos).node);
	if (!anchorCell) return false;

	const anchorRow = anchorCell.closest('tr');
	return !!anchorRow && anchorRow === rowEl;
};

// Show column menu when a cell in that column is selected (DOM-based)
export const isColumnActiveFromSelection = ({
	editor,
	view,
	state,
	from
}: {
	editor: Editor;
	view: EditorView;
	state: EditorState;
	from: number;
}) => {
	if (!editor.isActive(Table.name)) return false;

	// Container at the BubbleMenu anchor (header cell)
	const container = closestTableCell(view.nodeDOM(from) || view.domAtPos(from).node);
	if (!container) return false;

	const headerRow = container.closest('tr');
	if (!headerRow) return false;

	const headerCells = Array.from(headerRow.children).filter(
		(el) => el.tagName === 'TD' || el.tagName === 'TH'
	);
	const containerIndex = headerCells.indexOf(container);
	if (containerIndex < 0) return false;

	// Current selection anchor cell DOM index in its row
	const anchorCell = closestTableCell(view.domAtPos(state.selection.$from.pos).node);
	if (!anchorCell) return false;

	const anchorRow = anchorCell.closest('tr');
	if (!anchorRow) return false;
	const anchorCells = Array.from(anchorRow.children).filter(
		(el) => el.tagName === 'TD' || el.tagName === 'TH'
	);
	const anchorIndex = anchorCells.indexOf(anchorCell);

	return anchorIndex === containerIndex;
};

const getCurrentCellRect = (tr: Transaction) => {
	const table = findTable(tr.selection);
	if (!table || !isCellSelection(tr.selection)) return null;
	const map = TableMap.get(table.node);
	const cell = map.findCell(tr.selection.$anchorCell.pos - table.start);
	return { table, map, cell };
};

const getSpanMetrics = (n: Node | null | undefined) => {
	const rowspan: unknown = n?.attrs.rowspan;
	const colspan: unknown = n?.attrs.colspan;
	return {
		rowspan: typeof rowspan === 'number' ? rowspan : 1,
		colspan: typeof colspan === 'number' ? colspan : 1
	};
};

const hasSpans = (cellsA: CellInfo[], cellsB: CellInfo[]) => {
	if (cellsA.length !== cellsB.length) return true;
	// Disallow operation if any cell has rowspan/colspan > 1
	for (let i = 0; i < cellsA.length; i++) {
		const a = cellsA[i].node;
		const b = cellsB[i].node;
		const aSp = getSpanMetrics(a);
		const bSp = getSpanMetrics(b);
		if (aSp.rowspan !== 1 || aSp.colspan !== 1) return true;
		if (bSp.rowspan !== 1 || bSp.colspan !== 1) return true;
	}
	return false;
};

const swapCells = (tr: Transaction, sourceCells: CellInfo[], targetCells: CellInfo[]) => {
	for (let i = 0; i < sourceCells.length; i++) {
		const posA = tr.mapping.map(sourceCells[i].pos);
		const posB = tr.mapping.map(targetCells[i].pos);
		const nodeA = tr.doc.nodeAt(posA);
		const nodeB = tr.doc.nodeAt(posB);
		if (!nodeA || !nodeB) continue;
		tr = tr.replaceWith(posA, posA + nodeA.nodeSize, nodeB.copy(nodeB.content));
		const mappedB = tr.mapping.map(posB);
		tr = tr.replaceWith(mappedB, mappedB + nodeB.nodeSize, nodeA.copy(nodeA.content));
	}
	return tr;
};

export const moveColumnLeft = (tr: Transaction) => {
	const ctx = getCurrentCellRect(tr);
	if (!ctx) return tr;
	const { cell } = ctx;
	const source = cell.left;
	const target = source - 1;
	if (target < 0) return tr;

	const sourceCells = getCellsInColumn(source)(tr.selection);
	const targetCells = getCellsInColumn(target)(tr.selection);
	if (!sourceCells || !targetCells || hasSpans(sourceCells, targetCells)) return tr;
	return swapCells(tr, sourceCells, targetCells);
};

export const moveColumnRight = (tr: Transaction) => {
	const ctx = getCurrentCellRect(tr);
	if (!ctx) return tr;
	const { map, cell } = ctx;
	const source = cell.left;
	const target = source + 1;
	if (target >= map.width) return tr;

	const sourceCells = getCellsInColumn(source)(tr.selection);
	const targetCells = getCellsInColumn(target)(tr.selection);
	if (!sourceCells || !targetCells || hasSpans(sourceCells, targetCells)) return tr;
	return swapCells(tr, sourceCells, targetCells);
};

export const moveRowUp = (tr: Transaction) => {
	const ctx = getCurrentCellRect(tr);
	if (!ctx) return tr;
	const { cell } = ctx;
	const source = cell.top;
	const target = source - 1;
	if (target < 0) return tr;
	const sourceRow = getCellsInRow(source)(tr.selection);
	const targetRow = getCellsInRow(target)(tr.selection);
	if (!sourceRow || !targetRow || hasSpans(sourceRow, targetRow)) return tr;
	return swapCells(tr, sourceRow, targetRow);
};

export const moveRowDown = (tr: Transaction) => {
	const ctx = getCurrentCellRect(tr);
	if (!ctx) return tr;
	const { map, cell } = ctx;
	const source = cell.top;
	const target = source + 1;
	if (target >= map.height) return tr;
	const sourceRow = getCellsInRow(source)(tr.selection);
	const targetRow = getCellsInRow(target)(tr.selection);
	if (!sourceRow || !targetRow || hasSpans(sourceRow, targetRow)) return tr;
	return swapCells(tr, sourceRow, targetRow);
};
