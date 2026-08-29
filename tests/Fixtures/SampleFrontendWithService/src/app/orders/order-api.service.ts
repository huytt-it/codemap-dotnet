import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

/// Fixture for Review Fix Pass v1, "nối FE thiếu 1 hop": the HTTP call lives here, in a service - NOT in a
/// component - so scan-fe must walk one level of Angular DI to find the components that actually render to a
/// user (order-list.component.ts and order-admin.component.ts both inject this service).
@Injectable({ providedIn: 'root' })
export class OrderApiService {
  constructor(private http: HttpClient) {}

  cancel(id: number) {
    return this.http.delete(`/api/orders/${id}`);
  }
}
