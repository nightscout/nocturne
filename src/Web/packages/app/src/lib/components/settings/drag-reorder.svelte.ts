/**
 * Drag-to-reorder state for a settings list of rows. Each row binds the handlers of its index;
 * a drop hands the reordered copy of the list to `onreorder`.
 */
export class DragReorder<T> {
  draggedIndex: number | null = $state(null);
  dragOverIndex: number | null = $state(null);

  #items: () => readonly T[];
  #onreorder: (items: T[]) => void;

  constructor(items: () => readonly T[], onreorder: (items: T[]) => void) {
    this.#items = items;
    this.#onreorder = onreorder;
  }

  start(event: DragEvent, index: number): void {
    this.draggedIndex = index;
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = "move";
      event.dataTransfer.setData("text/plain", String(index));
    }
  }

  over(event: DragEvent, index: number): void {
    event.preventDefault();
    if (this.draggedIndex !== null && this.draggedIndex !== index) {
      this.dragOverIndex = index;
    }
  }

  leave(): void {
    this.dragOverIndex = null;
  }

  drop(event: DragEvent, targetIndex: number): void {
    event.preventDefault();
    if (this.draggedIndex !== null && this.draggedIndex !== targetIndex) {
      this.#onreorder(moveItem(this.#items(), this.draggedIndex, targetIndex));
    }
    this.end();
  }

  end(): void {
    this.draggedIndex = null;
    this.dragOverIndex = null;
  }
}

/** A copy of `items` with the item at `from` moved to `to`. */
export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  const next = [...items];
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);
  return next;
}
