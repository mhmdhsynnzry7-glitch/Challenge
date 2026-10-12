import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { apiErrorMessage } from '../core/api-error';
import { Product } from '../core/api.models';
import { ApiService } from '../core/api.service';

@Component({
  selector: 'app-products',
  imports: [FormsModule, DecimalPipe],
  template: `
    <h1>Products</h1>
    <form class="toolbar" (ngSubmit)="applyFilters()">
      <label>Search <input name="search" [(ngModel)]="search" [disabled]="loading" placeholder="Code or name" /></label>
      <label>Status
        <select name="active" [(ngModel)]="isActive" [disabled]="loading">
          <option [ngValue]="true">Active</option><option [ngValue]="false">Inactive</option>
        </select>
      </label>
      <button type="submit" [disabled]="loading">Search</button>
    </form>
    @if (loading) { <p role="status">Loading products…</p> }
    @if (error) { <p role="alert">{{ error }}</p> }
    <table>
      <thead>
        <tr>
          <th>Code</th>
          <th>Name</th>
          <th>Price</th>
          <th>Available Stock</th>
          <th>Status</th>
        </tr>
      </thead>
      <tbody>
        @for (product of products; track product.id) {
          <tr><td>{{ product.code }}</td><td>{{ product.name }}</td>
            <td>{{ product.price | number:'1.2-2' }}</td><td>{{ product.stockQuantity }}</td>
            <td>{{ product.isActive ? 'Active' : 'Inactive' }}</td></tr>
        } @empty {
          @if (!loading && !error) { <tr><td colspan="5">No products match your search.</td></tr> }
        }
      </tbody>
    </table>
    <div class="toolbar">
      <button type="button" (click)="prev()" [disabled]="loading || page <= 1">Previous</button>
      <span>Page {{ page }} · {{ totalCount }} products</span>
      <button type="button" (click)="next()" [disabled]="loading || page * pageSize >= totalCount">Next</button>
    </div>
  `
})
export class ProductsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);
  search = '';
  isActive = true;
  page = 1;
  readonly pageSize = 10;
  products: Product[] = [];
  totalCount = 0;
  loading = false;
  error = '';

  ngOnInit(): void { this.load(); }
  applyFilters(): void { if (!this.loading) { this.page = 1; this.load(); } }
  load(): void {
    if (this.loading) return;
    this.loading = true;
    this.error = '';
    this.products = [];
    this.api.products({ search: this.search.trim(), isActive: this.isActive, page: this.page, pageSize: this.pageSize })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loading = false)).subscribe({
        next: result => { this.products = result.items; this.totalCount = result.totalCount; this.page = result.page; },
        error: error => { this.totalCount = 0; this.error = apiErrorMessage(error); }
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
