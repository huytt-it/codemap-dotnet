import { Component } from '@angular/core';
import { OrderApiService } from '../orders/order-api.service';

@Component({ selector: 'app-order-admin' })
export class OrderAdminComponent {
  constructor(private orderApi: OrderApiService) {}

  forceCancel(id: number) {
    return this.orderApi.cancel(id);
  }
}
