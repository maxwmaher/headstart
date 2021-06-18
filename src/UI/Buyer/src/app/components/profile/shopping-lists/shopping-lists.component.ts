import { Component, OnInit } from '@angular/core'
import { Order, Me, ListPage } from 'ordercloud-javascript-sdk'

@Component({
  selector: 'app-shopping-lists',
  templateUrl: './shopping-lists.component.html',
  styleUrls: ['./shopping-lists.component.scss'],
})
export class ShoppingListsComponent implements OnInit {
  isLoading: boolean
  shoppingLists: ListPage<Order>
  listOptions: any = {
    filters: {
      'xp.OrderType': 'ShoppingList',
    },
  }

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
}
