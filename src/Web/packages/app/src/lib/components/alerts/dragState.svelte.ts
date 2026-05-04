import type { ConditionNode } from "./types";

// Module-level drag state. There's only ever one in-flight drag so a
// singleton is simpler than threading context through the recursive
// RuleBuilder tree. Components read `drag.active` reactively.
type DragSource = {
	parent: ConditionNode;
	index: number;
	node: ConditionNode;
};

class DragState {
	source = $state<DragSource | null>(null);
	// `${compositeUid}:${index}` of the row being hovered, or `${compositeUid}:end` for tail drop.
	overKey = $state<string | null>(null);

	begin(parent: ConditionNode, index: number): void {
		const node = parent.composite?.conditions[index];
		if (!node) return;
		this.source = { parent, index, node };
	}

	end(): void {
		this.source = null;
		this.overKey = null;
	}

	/** Is `candidate` (or any descendant) the node currently being dragged? */
	containsSource(candidate: ConditionNode): boolean {
		if (!this.source) return false;
		const target = this.source.node;
		const visit = (n: ConditionNode): boolean => {
			if (n === target) return true;
			if (n.composite?.conditions) {
				for (const c of n.composite.conditions) if (visit(c)) return true;
			}
			if (n.not?.child && visit(n.not.child)) return true;
			if (n.sustained?.child && visit(n.sustained.child)) return true;
			return false;
		};
		return visit(candidate);
	}

	/** Move source into `destParent` at `destIndex`. No-op if invalid. */
	dropInto(destParent: ConditionNode, destIndex: number): void {
		const src = this.source;
		this.end();
		if (!src) return;
		if (!destParent.composite) return;
		// Refuse to drop a group into itself or any of its descendants — would
		// detach the subtree from the editor's view.
		if (this.wouldOrphan(src.node, destParent)) return;

		const fromList = src.parent.composite?.conditions;
		const toList = destParent.composite.conditions;
		if (!fromList) return;

		// Snapshot the node before the source splice — Svelte's deep proxy
		// re-resolves indices after mutation.
		const moving = src.node;

		if (fromList === toList) {
			// Same-list move: account for the index shift caused by removal.
			const adjusted = destIndex > src.index ? destIndex - 1 : destIndex;
			fromList.splice(src.index, 1);
			toList.splice(Math.max(0, Math.min(adjusted, toList.length)), 0, moving);
		} else {
			fromList.splice(src.index, 1);
			toList.splice(Math.max(0, Math.min(destIndex, toList.length)), 0, moving);
		}
	}

	private wouldOrphan(source: ConditionNode, dest: ConditionNode): boolean {
		if (source === dest) return true;
		if (source.composite?.conditions) {
			for (const c of source.composite.conditions) {
				if (this.wouldOrphan(c, dest)) return true;
			}
		}
		if (source.not?.child && this.wouldOrphan(source.not.child, dest))
			return true;
		if (source.sustained?.child && this.wouldOrphan(source.sustained.child, dest))
			return true;
		return false;
	}
}

export const drag = new DragState();
