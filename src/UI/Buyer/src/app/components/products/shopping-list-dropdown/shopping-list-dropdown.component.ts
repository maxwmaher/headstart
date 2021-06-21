import { Component, Input } from '@angular/core'
import { FormGroup } from '@angular/forms'
import { Router } from '@angular/router'
import {
  ListPage,
  Order,
  BuyerProduct,
  LineItems,
  Me,
  Orders,
  Spec,
} from 'ordercloud-javascript-sdk'
import { ModalState } from 'src/app/models/shared.types'
import { ShopperContextService } from 'src/app/services/shopper-context/shopper-context.service'
import { SuperHSProduct } from '@ordercloud/headstart-sdk'
import { SpecFormService } from '../spec-form/spec-form.service'

@Component({
  selector: 'app-shopping-list-dropdown',
  templateUrl: './shopping-list-dropdown.component.html',
  styleUrls: ['./shopping-list-dropdown.component.scss'],
})
export class ShoppingListDropdownComponent {
  orderID: string
  newShoppingListName: string
  shoppingListModal: ModalState = ModalState.Closed
  createShoppingListModal: ModalState = ModalState.Closed
  alreadyAddedToList: boolean
  shoppingLists: ListPage<Order>
  listOptions: any = {
    filters: {
      'xp.OrderType': 'ShoppingList',
    },
  }
  @Input() product: BuyerProduct
  @Input() superProduct: SuperHSProduct
  @Input() specs: Spec[]
  @Input() specForm: FormGroup

  constructor(
    private router: Router,
    private context: ShopperContextService,
    private specFormService: SpecFormService
  ) {}

  ngOnInit(): void {
    if (!this.shoppingLists) {
      void this.getShoppingLists()
    }
  }

  async listSelected(orderID: string): Promise<void> {
    if (!orderID) {
      return
    } else if (orderID === 'CREATE_NEW_SHOPPING_LIST') {
      void this.openCreateShoppingListModal()
    } else {
      this.orderID = orderID
      const lineItems = await LineItems.List('Outgoing', this.orderID, {
        pageSize: 100,
      })
      this.alreadyAddedToList = lineItems.Items.map(
        (li) => li.ProductID
      ).includes(this.superProduct.Product.ID)
      if (!this.alreadyAddedToList) {
        await this.context.order.cart.add(
          {
            ProductID: this.superProduct.Product.ID,
            Quantity: 1,
            Specs: this.specFormService.getLineItemSpecs(
              this.specs,
              this.specForm
            ),
            xp: {
              ImageUrl: this.specFormService.getLineItemImageUrl(
                this.superProduct.Product?.xp?.Images,
                this.superProduct.Specs,
                this.specForm
              ),
            },
          },
          this.orderID
        )
      }
      this.shoppingListModal = ModalState.Open
    }
  }

  async getShoppingLists(): Promise<void> {
    this.shoppingLists = await Me.ListOrders(this.listOptions)
  }

  openCreateShoppingListModal(): void {
    this.createShoppingListModal = ModalState.Open
  }

  dismissShoppingListModal(): void {
    this.shoppingListModal = ModalState.Closed
  }

  viewShoppingList(): void {
    this.dismissShoppingListModal()
    void this.router.navigate([`/profile/shopping-lists/${this.orderID}`])
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
    this.orderID = orderResponse.ID
    this.createShoppingListModal = ModalState.Closed
    this.shoppingLists.Items.push(orderResponse)
  }
}
