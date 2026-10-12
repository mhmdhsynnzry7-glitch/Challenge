import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { environment } from '../../environments/environment';
import { CreateOrderRequest, CreateOrderResponse, Customer, OrderDetails, OrderQuery,
  OrderSummary, Page, Product, ProductQuery } from './api.models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = environment.apiUrl + '/api';

  products(query: ProductQuery) {
    return this.http.get<Page<Product>>(this.baseUrl + '/products', { params: this.params(query) });
  }
  customers() { return this.http.get<Customer[]>(this.baseUrl + '/customers'); }
  orders(query: OrderQuery) {
    return this.http.get<Page<OrderSummary>>(this.baseUrl + '/orders', { params: this.params(query) });
  }
  order(id: number) { return this.http.get<OrderDetails>(this.baseUrl + '/orders/' + id); }
  createOrder(request: CreateOrderRequest) {
    // Explicitly project the permitted fields even if a caller passes an object with extra properties.
    const body: CreateOrderRequest = { customerId: request.customerId,
      items: request.items.map(item => ({ productId: item.productId, quantity: item.quantity })) };
    return this.http.post<CreateOrderResponse>(this.baseUrl + '/orders', body);
  }
  private params(query: object): HttpParams {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') params = params.set(key, String(value));
    }
    return params;
  }
}
