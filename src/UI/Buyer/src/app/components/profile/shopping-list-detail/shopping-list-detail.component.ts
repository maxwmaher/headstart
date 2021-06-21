import { Component, OnInit } from '@angular/core'
import { ActivatedRoute, Router } from '@angular/router'
import {
  LineItem,
  LineItemProduct,
  LineItems,
  Me,
  Order,
  Orders,
} from 'ordercloud-javascript-sdk'
import { ListPage } from '../../../../../../SDK/dist'
import { uniq } from 'lodash'
import { faTrashAlt } from '@fortawesome/free-regular-svg-icons'
import { Subject } from 'rxjs'
import { debounceTime, distinctUntilChanged } from 'rxjs/operators'
import { ToastrService } from 'ngx-toastr'

@Component({
  selector: 'app-shopping-list-detail',
  templateUrl: './shopping-list-detail.component.html',
  styleUrls: ['./shopping-list-detail.component.scss'],
})
export class ShoppingListDetailComponent implements OnInit {
  isLoading: boolean
  order?: Order
  lineItems?: ListPage<LineItem>
  faTrashAlt = faTrashAlt
  shoppingListNameChanged: Subject<string> = new Subject<string>()
  constructor(
    private router: Router,
    private activatedRoute: ActivatedRoute,
    private toastrService: ToastrService
  ) {}

  ngOnInit(): void {
    this.isLoading = true
    const orderID = this.activatedRoute.snapshot.paramMap.get('orderID')
    void this.getOrderDetails(orderID)

    this.shoppingListNameChanged
      .pipe(debounceTime(500), distinctUntilChanged())
      .subscribe(async (updatedName) => {
        this.order = await Orders.Patch('Outgoing', this.order.ID, {
          xp: { ShoppingListName: updatedName },
        })
      })
  }

  onShoppingListNameChange(newName: string): void {
    this.shoppingListNameChanged.next(newName)
  }

  async deleteShoppingList(): Promise<void> {
    await Orders.Delete('Outgoing', this.order.ID)
    this.toastrService.success('Shopping list deleted', 'Success')
    void this.router.navigate(['/profile/shopping-lists'])
  }

  async getOrderDetails(orderID: string): Promise<void> {
    try {
      const orderResponse = await Me.ListOrders({
        filters: { ID: orderID },
      })
      this.order = orderResponse.Items.length ? orderResponse.Items[0] : null
      this.lineItems = await LineItems.List('Outgoing', orderID, {
        pageSize: 100,
      })
      const productIDs = uniq(this.lineItems.Items.map((p) => p.ProductID))
      const buyerProducts = await Me.ListProducts({
        filters: { ID: productIDs.join('|') },
      })
      this.lineItems.Items = this.lineItems.Items.map((li) => {
        // eslint-disable-next-line @typescript-eslint/no-unsafe-member-access
        ;(li as any).Product = buyerProducts.Items.find(
          (p) => p.ID === li.ProductID
        ) as LineItemProduct
        return li
      })
    } finally {
      this.isLoading = false
    }
  }
}
