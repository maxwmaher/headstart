import { LineItemStatus } from '@app-seller/models/order.types'
import { HSLineItem, HSOrderReturn } from '@ordercloud/headstart-sdk'
import { flatten, sum } from 'lodash'

const validPreviousStates = {
  Submitted: [],
  Complete: [LineItemStatus.Submitted, LineItemStatus.Backordered],
  Backordered: [LineItemStatus.Submitted],
}

export function NumberCanChangeTo(
  lineItemStatus: LineItemStatus,
  lineItem: HSLineItem
): number {
  return Object.entries(lineItem.xp.StatusByQuantity as any)
    .filter(([status, quantity]) =>
      validPreviousStates[lineItemStatus].includes(status)
    )
    .reduce((acc, [status, quantity]) => (quantity as number) + acc, 0)
}
export function CanChangeTo(
  lineItemStatus: LineItemStatus,
  lineItem: HSLineItem
): boolean {
  return !!NumberCanChangeTo(lineItemStatus, lineItem)
}

export function CanChangeLineItemsOnOrderTo(
  lineItemStatus: LineItemStatus,
  lineItems: HSLineItem[]
): boolean {
  return (
    !lineItems.some(
      (li) =>
        lineItemStatus === LineItemStatus.Backordered &&
        li.Product?.xp?.ProductType === 'Quote'
    ) && lineItems.some((li) => CanChangeTo(lineItemStatus, li))
  )
}

export function SellerOrderCanShip(
  lineItemStatus: LineItemStatus,
  lineItems: HSLineItem[],
  sellerUser: boolean
): boolean {
  return lineItems.some((li) => li.SupplierID === null && sellerUser)
}

export function NumberCanReturn(
  lineItem: HSLineItem,
  orderReturns: HSOrderReturn[] = []
): number {
  if (!lineItem.Product.Returnable) {
    return 0
  }
  const alreadyReturnedItems = flatten(
    orderReturns
      .filter(
        (orderReturn) =>
          orderReturn.Status === 'Open' ||
          orderReturn.Status === 'AwaitingApproval' ||
          orderReturn.Status === 'Completed'
      )
      .map((orderReturn) => orderReturn.ItemsToReturn)
  ).filter((item) => item.LineItemID === lineItem.ID)
  const maxReturnable = lineItem.QuantityShipped
  const quantityAlreadyRequested = sum(
    alreadyReturnedItems.filter((li) => li.LineItemID).map((li) => li.Quantity)
  )
  return maxReturnable - quantityAlreadyRequested
}

export function CanReturn(
  lineItem: HSLineItem,
  orderReturns: HSOrderReturn[] = []
): boolean {
  return !!NumberCanReturn(lineItem, orderReturns)
}

export function CanReturnOrder(
  lineItems: HSLineItem[],
  orderReturns: HSOrderReturn[] = []
): boolean {
  return lineItems.some((li) => CanReturn(li, orderReturns))
}
