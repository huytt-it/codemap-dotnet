import { Component } from '@angular/core';
import { OrderApiService } from './order-api.service';

@Component({ selector: 'app-order-list' })
export class OrderListComponent {
  constructor(private orderApi: OrderApiService) {}

  cancelOrder(id: number) {
    return this.orderApi.cancel(id);
  }
}
