import { HttpClient } from '@angular/common/http';

/// Fixture for scan-fe's Angular strategy (spec section 9): calls the same DELETE api/orders/{id} endpoint as
/// OrdersController.Delete in the backend fixture, from feature "orders".
export class OrderListComponent {
  constructor(private http: HttpClient) {}

  cancelOrder(id: number) {
    return this.http.delete(`/api/orders/${id}`);
  }
}
