import {
  AlertReceiptActorKind,
  type AlertReceiptResponse,
} from "@nocturne/api-client";
import { formatList } from "#lib/utils/formatting.js";

/** How one receipt names who saw the alarm. */
export function receiptActor(receipt: AlertReceiptResponse): string {
  switch (receipt.actorKind) {
    case AlertReceiptActorKind.PublicLink:
      return "Public link";
    case AlertReceiptActorKind.Guest:
      return `${receipt.actorLabel || "Guest"} (guest link)`;
    case AlertReceiptActorKind.Clock:
      // @wc-include
      return `${receipt.actorLabel || "Clock"} (clock)`;
    default:
      return receipt.actorLabel || "A member";
  }
}

/** "Seen by Public link and Grandma (guest link)", or null when nobody has. */
export function seenBy(receipts: AlertReceiptResponse[] | undefined): string | null {
  if (!receipts?.length) return null;
  return `Seen by ${formatList(receipts.map(receiptActor))}`;
}
