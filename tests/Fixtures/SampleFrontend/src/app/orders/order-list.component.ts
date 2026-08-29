import { Component } from '@angular/core';
import { HttpClient } from '@angular/common/http';

/// Fixture for scan-fe's Angular strategy (spec section 9): calls the same DELETE api/orders/{id} endpoint as
/// OrdersController.Delete in the backend fixture, from feature "orders". @Component marks this as a screen
/// itself (Review Fix Pass v1, "nối FE thiếu 1 hop") — a call directly inside a component needs no further
/// resolution, unlike a call inside a plain service class (see SampleFrontendWithService).
@Component({ selector: 'app-order-list' })
export class OrderListComponent {
  constructor(private http: HttpClient) {}

  cancelOrder(id: number) {
    return this.http.delete(`/api/orders/${id}`);
  }
}
