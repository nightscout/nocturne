import { describe, it, expect } from 'vitest';
import { getSchema, type JSONContent } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { EditorState } from '@tiptap/pm/state';
import { CellSelection } from '@tiptap/pm/tables';
import { Table, TableCell, TableHeader, TableRow } from './index.ts';
import { moveColumnLeft, moveColumnRight, moveRowDown, moveRowUp } from './utils.ts';

const schema = getSchema([StarterKit, Table, TableRow, TableHeader, TableCell]);

const cell = (text: string): JSONContent => ({
	type: 'tableCell',
	content: [{ type: 'paragraph', content: [{ type: 'text', text }] }]
});

const grid = (rows: string[][]) =>
	schema.nodeFromJSON({
		type: 'doc',
		content: [
			{
				type: 'table',
				content: rows.map((r) => ({ type: 'tableRow', content: r.map(cell) }))
			}
		]
	});

/** State with a single-cell CellSelection on the cell containing `text`. */
const stateAt = (rows: string[][], text: string) => {
	const doc = grid(rows);
	let pos = -1;
	doc.descendants((node, p) => {
		if (node.type.name === 'tableCell' && node.textContent === text) pos = p;
	});
	const state = EditorState.create({ doc, schema });
	return state.apply(state.tr.setSelection(CellSelection.create(doc, pos)));
};

const texts = (state: EditorState) => {
	const rows: string[][] = [];
	state.doc.descendants((node) => {
		if (node.type.name === 'tableRow') {
			const row: string[] = [];
			node.forEach((c) => row.push(c.textContent));
			rows.push(row);
		}
	});
	return rows;
};

const rows = [
	['a', 'b', 'c'],
	['d', 'e', 'f']
];

describe('table move commands', () => {
	it('moves a column left and right', () => {
		const s = stateAt(rows, 'b');
		expect(texts(s.apply(moveColumnLeft(s.tr)))).toEqual([
			['b', 'a', 'c'],
			['e', 'd', 'f']
		]);
		expect(texts(s.apply(moveColumnRight(s.tr)))).toEqual([
			['a', 'c', 'b'],
			['d', 'f', 'e']
		]);
	});

	it('moves a row up and down', () => {
		const s = stateAt(rows, 'e');
		expect(texts(s.apply(moveRowUp(s.tr)))).toEqual([
			['d', 'e', 'f'],
			['a', 'b', 'c']
		]);
		const top = stateAt(rows, 'a');
		expect(texts(top.apply(moveRowDown(top.tr)))).toEqual([
			['d', 'e', 'f'],
			['a', 'b', 'c']
		]);
	});

	it('leaves the table alone at an edge', () => {
		const s = stateAt(rows, 'a');
		expect(texts(s.apply(moveColumnLeft(s.tr)))).toEqual(rows);
		expect(texts(s.apply(moveRowUp(s.tr)))).toEqual(rows);
	});
});
