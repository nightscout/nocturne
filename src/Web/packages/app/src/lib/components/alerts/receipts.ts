import {
  AlertReceiptActorKind,
  type AlertReceiptResponse,
} from "#lib/api/generated/nocturne-api-client.js";

/** How one receipt names who saw the alarm. */
export function receiptActor(receipt: AlertReceiptResponse): string {
  switch (receipt.actorKind) {
    case AlertReceiptActorKind.PublicLink:
      return "Public link";
    case AlertReceiptActorKind.Guest:
      return `${receipt.actorLabel || "Guest"} (guest link)`;
    case AlertReceiptActorKind.Clock:
      return `${receipt.actorLabel || "Clock"} (clock)`;
    default:
      return receipt.actorLabel || "A member";
  }
}

/** "Seen by Public link, Grandma (guest link)", or null when nobody has. */
export function seenBy(receipts: AlertReceiptResponse[] | undefined): string | null {
  if (!receipts?.length) return null;
  return `Seen by ${receipts.map(receiptActor).join(", ")}`;
}
