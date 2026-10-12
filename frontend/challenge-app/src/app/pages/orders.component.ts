import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { apiErrorMessage } from '../core/api-error';
import { OrderSort, OrderStatus, OrderSummary } from '../core/api.models';
import { ApiService } from '../core/api.service';

@Component({
  selector: 'app-orders',
  imports: [FormsModule, RouterLink, DatePipe, DecimalPipe],
  template: `
    <h1>Orders</h1>
    <p><a routerLink="/orders/create">Create order</a></p>
    <form class="toolbar" (ngSubmit)="applyFilters()">
      <label>Order number <input name="number" [(ngModel)]="orderNumber" [disabled]="loading" /></label>
      <label>Customer ID <input name="customer" type="number" min="1" step="1" [(ngModel)]="customerId" [disabled]="loading" /></label>
      <label>Status <select name="status" [(ngModel)]="status" [disabled]="loading">
        <option value="">All statuses</option>
        <option value="Confirmed">Confirmed</option>
        <option value="Cancelled">Cancelled</option>
      </select></label>
      <label>From date (UTC) <input name="from" type="date" [(ngModel)]="fromDate" [disabled]="loading" /></label>
      <label>To date (UTC) <input name="to" type="date" [(ngModel)]="toDate" [disabled]="loading" /></label>
      <label>Sort by <select name="sort" [(ngModel)]="sortBy" [disabled]="loading">
        <option value="orderDate">Date</option><option value="orderNumber">Order number</option>
        <option value="totalAmount">Total amount</option>
      </select></label>
      <label>Direction <select name="direction" [(ngModel)]="sortDirection" [disabled]="loading">
        <option value="desc">Descending</option><option value="asc">Ascending</option>
      </select></label>
      <button type="submit" [disabled]="loading">Filter</button>
    </form>
    @if (loading) { <p role="status">Loading orders…</p> }
    @if (error) { <p role="alert">{{ error }}</p> }
    <table>
      <thead>
        <tr>
          <th>Order Number</th>
          <th>Customer</th>
          <th>Date (UTC)</th>
          <th>Total</th>
          <th>Status</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        @for (order of orders; track order.id) {
          <tr><td>{{ order.orderNumber }}</td><td>{{ order.customerName }}</td>
            <td>{{ order.orderDate | date:'medium':'UTC' }}</td>
            <td>{{ order.totalAmount | number:'1.2-2' }}</td><td>{{ order.status }}</td>
            <td><a [routerLink]="['/orders', order.id]">Details</a></td></tr>
        } @empty {
          @if (!loading && !error) { <tr><td colspan="6">No orders match your filters.</td></tr> }
        }
      </tbody>
    </table>
    <div class="toolbar">
      <button type="button" (click)="prev()" [disabled]="loading || page <= 1">Previous</button>
      <span>Page {{ page }} · {{ totalCount }} orders</span>
      <button type="button" (click)="next()" [disabled]="loading || page * pageSize >= totalCount">Next</button>
    </div>
  `
})
export class OrdersComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);
  orderNumber = '';
  customerId: number | null = null;
  status: OrderStatus | '' = '';
  fromDate = '';
  toDate = '';
  sortBy: OrderSort = 'orderDate';
  sortDirection: 'asc' | 'desc' = 'desc';
  page = 1;
  readonly pageSize = 10;
  totalCount = 0;
  orders: OrderSummary[] = [];
  loading = false;
  error = '';

  ngOnInit(): void { this.load(); }
  applyFilters(): void { if (!this.loading) { this.page = 1; this.load(); } }
  load(): void {
    if (this.loading) return;
    this.error = '';
    this.orders = [];
    this.totalCount = 0;
    if (this.fromDate && this.toDate && this.fromDate > this.toDate) {
      this.error = 'From date must be on or before to date.';
      return;
    }
    if (this.customerId !== null && (!Number.isInteger(this.customerId) || this.customerId < 1 || this.customerId > 2_147_483_647)) {
      this.error = 'Customer ID must be a positive integer.';
      return;
    }
    this.loading = true;
    this.api.orders({ orderNumber: this.orderNumber.trim(), customerId: this.customerId, status: this.status,
      fromDate: this.fromDate, toDate: this.toDate, sortBy: this.sortBy, sortDirection: this.sortDirection,
      page: this.page, pageSize: this.pageSize }).pipe(takeUntilDestroyed(this.destroyRef),
      finalize(() => this.loading = false)).subscribe({
        next: result => { this.orders = result.items; this.totalCount = result.totalCount; this.page = result.page; },
        error: error => { this.error = apiErrorMessage(error); }
      });
  }
  prev(): void {
    if (!this.loading && this.page > 1) {
      this.page -= 1;
      this.load();
    }
  }
  next(): void {
    if (!this.loading && this.page * this.pageSize < this.totalCount) { this.page += 1; this.load(); }
  }
}
