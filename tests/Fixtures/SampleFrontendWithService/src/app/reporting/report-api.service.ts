import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

/// Fixture for the OTHER half of "nối FE thiếu 1 hop": nobody's constructor injects this service directly
/// (deliberately - simulates being injected into another service, or a module-level provider) - scan-fe must
/// leave injectedBy empty and log it to diagnostics.json instead of guessing.
@Injectable({ providedIn: 'root' })
export class ReportApiService {
  constructor(private http: HttpClient) {}

  generate() {
    return this.http.get('/api/reports/summary');
  }
}
