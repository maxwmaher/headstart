import { Component, OnInit } from '@angular/core'
import { Order, Me, ListPage, Orders } from 'ordercloud-javascript-sdk'
import { ModalState } from 'src/app/models/shared.types'

@Component({
  selector: 'app-shopping-lists',
  templateUrl: './shopping-lists.component.html',
  styleUrls: ['./shopping-lists.component.scss'],
})
export class ShoppingListsComponent implements OnInit {
  isLoading: boolean
  newShoppingListName: string
  shoppingLists: ListPage<Order>
  listOptions: any = {
    filters: {
      'xp.OrderType': 'ShoppingList',
    },
  }
  createShoppingListModal: ModalState = ModalState.Closed

  ngOnInit(): void {
    this.isLoading = true
    void this.getShoppingLists()
  }

  async getShoppingLists(): Promise<void> {
    try {
      this.shoppingLists = await Me.ListOrders(this.listOptions)
    } finally {
      this.isLoading = false
    }
  }

  updateListOptions(newOptions: { page?: number; search?: string }): void {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-assignment
    this.listOptions = Object.assign(this.listOptions, newOptions)
    void this.getShoppingLists()
  }

  openCreateShoppingListModal(): void {
    this.createShoppingListModal = ModalState.Open
  }

  dismissCreateShoppingListModal(): void {
    this.newShoppingListName = ''
    this.createShoppingListModal = ModalState.Closed
  }

  async createNewShoppingList(): Promise<void> {
    const newOrder = {
      xp: {
        ShoppingListName: this.newShoppingListName,
        OrderType: 'ShoppingList',
      },
    }
    const orderResponse = await Orders.Create('Outgoing', newOrder)
    this.createShoppingListModal = ModalState.Closed
    this.shoppingLists.Items.push(orderResponse)
  }
}
