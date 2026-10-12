import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, of, switchMap } from 'rxjs';
import { apiErrorMessage } from '../core/api-error';
import { OrderDetails } from '../core/api.models';
import { ApiService } from '../core/api.service';

@Component({
  selector: 'app-order-details',
  imports: [RouterLink, DatePipe, DecimalPipe],
  template: `
    <h1>Order details</h1>
    <p><a routerLink="/orders">Back to orders</a></p>
    @if (loading) { <p role="status">Loading order…</p> }
    @if (error) { <p role="alert">{{ error }}</p> }
    @if (order; as detail) {
    <dl>
      <dt>Order Number</dt>
      <dd>{{ detail.orderNumber }}</dd>
      <dt>Customer</dt>
      <dd>{{ detail.customer.code }} — {{ detail.customer.name }}</dd>
      <dt>Created by</dt>
      <dd>{{ detail.createdByUser.username }}</dd>
      <dt>Date (UTC)</dt>
      <dd>{{ detail.orderDate | date:'medium':'UTC' }}</dd>
      <dt>Status</dt>
      <dd>{{ detail.status }}</dd>
    </dl>
    <table>
      <thead>
        <tr>
          <th>Product</th>
          <th>Quantity</th>
          <th>Unit Price</th>
          <th>Total</th>
        </tr>
      </thead>
      <tbody>
        @for (item of detail.items; track item.productId) {
          <tr><td>{{ item.productCode }} — {{ item.productName }}</td><td>{{ item.quantity }}</td>
            <td>{{ item.unitPrice | number:'1.2-2' }}</td><td>{{ item.totalPrice | number:'1.2-2' }}</td></tr>
        } @empty { <tr><td colspan="4">No order items.</td></tr> }
      </tbody>
    </table>
    <p><strong>Order total:</strong> {{ detail.totalAmount | number:'1.2-2' }}</p>
    }
  `
})
export class OrderDetailsComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);
  order: OrderDetails | null = null;
  loading = false;
  error = '';

  ngOnInit(): void {
    this.route.paramMap.pipe(switchMap(params => {
      this.order = null;
      this.error = '';
      this.loading = false;
      const rawId = params.get('id') ?? '';
      const id = Number(rawId);
      if (!/^[1-9]\d*$/.test(rawId) || !Number.isInteger(id) || id > 2_147_483_647) {
        this.error = 'Invalid order ID.';
        return of(null);
      }
      this.loading = true;
      return this.api.order(id).pipe(catchError(error => {
        this.error = error instanceof HttpErrorResponse && error.status === 404
          ? 'Order was not found.' : apiErrorMessage(error);
        return of(null);
      }), finalize(() => this.loading = false));
    }), takeUntilDestroyed(this.destroyRef)).subscribe(order => this.order = order);
  }
}
