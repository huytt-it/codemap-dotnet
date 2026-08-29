import { Component } from '@angular/core';
import { HttpClient } from '@angular/common/http';

/// Second feature calling the SAME backend endpoint as order-list.component.ts — spec section 9's fixture
/// design goal: "impact trên method đáy trả đúng 3 entry point + 2 màn hình FE" (2 distinct FE screens).
/// @Component marks this as a screen itself — see order-list.component.ts's comment.
@Component({ selector: 'app-order-admin' })
export class OrderAdminComponent {
  constructor(private httpClient: HttpClient) {}

  forceCancel(id: number) {
    return this.httpClient.delete(`/api/orders/${id}`);
  }
}
