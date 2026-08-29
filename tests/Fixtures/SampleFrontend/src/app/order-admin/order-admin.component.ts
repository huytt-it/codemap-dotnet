import { HttpClient } from '@angular/common/http';

/// Second feature calling the SAME backend endpoint as order-list.component.ts — spec section 9's fixture
/// design goal: "impact trên method đáy trả đúng 3 entry point + 2 màn hình FE" (2 distinct FE screens).
export class OrderAdminComponent {
  constructor(private httpClient: HttpClient) {}

  forceCancel(id: number) {
    return this.httpClient.delete(`/api/orders/${id}`);
  }
}
