import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize, forkJoin } from 'rxjs';
import { apiErrorMessage } from '../core/api-error';
import { Customer, Page, Product } from '../core/api.models';
import { ApiService } from '../core/api.service';

interface OrderLine { productId: number | null; quantity: number | null; product: Product | null; }

@Component({
  selector: 'app-order-create',
  imports: [FormsModule, RouterLink, DecimalPipe],
  template: `
    <h1>Create order</h1>
    @if (loading) { <p role="status">Loading customers and products…</p> }
    @if (loadingProducts) { <p role="status">Loading products…</p> }
    @if (message) { <p role="alert">{{ message }}</p> }
    <button type="button" (click)="loadOptions()" [disabled]="busy">Reload customers and products</button>
    <form class="toolbar" (ngSubmit)="applyProductSearch()">
      <label>Find products
        <input name="search" [(ngModel)]="productSearch" [disabled]="busy" placeholder="Code or name" />
      </label>
      <button type="submit" [disabled]="busy">Search products</button>
      <button type="button" (click)="prevProducts()" [disabled]="busy || productPage <= 1">Previous products</button>
      <span>Product page {{ productPage }} · {{ productCount }} products</span>
      <button type="button" (click)="nextProducts()" [disabled]="busy || productPage * pageSize >= productCount">Next products</button>
    </form>
    @if (!loading && !loadingProducts && !message && products.length === 0) { <p>No active products match your search.</p> }
    @if (!loading && !message && customers.length === 0) { <p>No active customers are available.</p> }
    <form (ngSubmit)="submit()">
    <fieldset [disabled]="busy">
    <label>
      Customer
      <select name="customer" [(ngModel)]="customerId" required>
        <option [ngValue]="null">Select a customer</option>
        @for (customer of customers; track customer.id) {
          <option [ngValue]="customer.id">{{ customer.code }} — {{ customer.name }}</option>
        }
      </select>
    </label>
    <table>
      <thead>
        <tr>
          <th>Product</th>
          <th>Quantity</th>
          <th>Unit price</th>
          <th>Line total</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        @for (line of lines; track line; let i = $index) {
          <tr>
            <td>
              <select [name]="'product-' + i" [(ngModel)]="line.productId" (ngModelChange)="selectProduct(line)"
                [attr.aria-label]="'Product for item ' + (i + 1)" required>
                <option [ngValue]="null">Select product</option>
                @for (product of optionsFor(line); track product.id) {
                  <option [ngValue]="product.id" [disabled]="selectedElsewhere(product.id, line)">
                    {{ product.code }} — {{ product.name }} (stock {{ product.stockQuantity }})
                  </option>
                }
              </select>
            </td>
            <td><input [name]="'quantity-' + i" type="number" min="1" max="2147483647" step="1"
              [(ngModel)]="line.quantity" [attr.aria-label]="'Quantity for item ' + (i + 1)" required /></td>
            <td>{{ (line.product?.price ?? 0) | number:'1.2-2' }}</td>
            <td>{{ lineTotal(line) | number:'1.2-2' }}</td>
            <td><button type="button" (click)="removeLine(line)">Remove</button></td>
          </tr>
        }
      </tbody>
    </table>
    <button type="button" (click)="addLine()">Add product</button>
    <p><strong>Estimated order total:</strong> {{ total | number:'1.2-2' }}</p>
    <p class="hint">Final prices and availability are confirmed when you submit.</p>
    <button type="submit" [disabled]="customers.length === 0">{{ submitting ? 'Submitting…' : 'Submit' }}</button>
    </fieldset>
    </form>
    <p><a routerLink="/orders">Back to orders</a></p>
  `
})
export class OrderCreateComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  customerId: number | null = null;
  customers: Customer[] = [];
  products: Product[] = [];
  lines: OrderLine[] = [{ productId: null, quantity: 1, product: null }];
  productSearch = '';
  productPage = 1;
  readonly pageSize = 10;
  productCount = 0;
  loading = false;
  loadingProducts = false;
  submitting = false;
  message = '';

  get busy(): boolean { return this.loading || this.loadingProducts || this.submitting; }
  ngOnInit(): void { this.loadOptions(); }
  loadOptions(): void {
    if (this.busy) return;
    this.loading = true;
    this.message = '';
    forkJoin({ customers: this.api.customers(), products: this.api.products({ search: this.productSearch.trim(),
      isActive: true, page: this.productPage, pageSize: this.pageSize }) })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loading = false)).subscribe({
        next: result => { this.customers = result.customers; this.acceptProducts(result.products); },
        error: error => { this.message = apiErrorMessage(error); }
      });
  }

  applyProductSearch(): void { if (!this.busy) { this.productPage = 1; this.loadProducts(); } }
  prevProducts(): void { if (!this.busy && this.productPage > 1) { this.productPage--; this.loadProducts(); } }
  nextProducts(): void {
    if (!this.busy && this.productPage * this.pageSize < this.productCount) { this.productPage++; this.loadProducts(); }
  }
  private loadProducts(): void {
    this.loadingProducts = true;
    this.message = '';
    this.products = [];
    this.productCount = 0;
    this.api.products({ search: this.productSearch.trim(), isActive: true, page: this.productPage, pageSize: this.pageSize })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.loadingProducts = false)).subscribe({
        next: result => this.acceptProducts(result),
        error: error => { this.message = apiErrorMessage(error); }
      });
  }
  private acceptProducts(result: Page<Product>): void {
    this.products = result.items;
    this.productCount = result.totalCount;
    this.productPage = result.page;
    for (const line of this.lines) {
      const product = this.products.find(item => item.id === line.productId);
      if (product) line.product = product;
    }
  }
  optionsFor(line: OrderLine): Product[] {
    return line.product && !this.products.some(item => item.id === line.productId)
      ? [...this.products, line.product] : this.products;
  }
  selectedElsewhere(id: number, line: OrderLine): boolean {
    return this.lines.some(other => other !== line && other.productId === id);
  }
  selectProduct(line: OrderLine): void {
    line.product = this.optionsFor(line).find(product => product.id === line.productId) ?? null;
  }
  lineTotal(line: OrderLine): number {
    return Number.isFinite(line.quantity) && (line.quantity ?? 0) > 0 ? (line.quantity ?? 0) * (line.product?.price ?? 0) : 0;
  }

  get total(): number {
    return this.lines.reduce((sum, line) => sum + this.lineTotal(line), 0);
  }

  addLine(): void {
    if (!this.busy) this.lines = [...this.lines, { productId: null, quantity: 1, product: null }];
  }
  removeLine(line: OrderLine): void { if (!this.busy) this.lines = this.lines.filter(item => item !== line); }

  submit(): void {
    if (this.busy) return;
    this.message = '';
    if (!this.customers.some(customer => customer.id === this.customerId)) {
      this.message = 'Select an active customer.'; return;
    }
    if (this.lines.length === 0) { this.message = 'Add at least one product.'; return; }
    if (this.lines.some(line => !line.product || line.product.id !== line.productId ||
        line.quantity === null || !Number.isInteger(line.quantity) || line.quantity < 1 || line.quantity > 2_147_483_647)) {
      this.message = 'Select a product and a positive whole-number quantity for every item.'; return;
    }
    if (new Set(this.lines.map(line => line.productId)).size !== this.lines.length) {
      this.message = 'Each product may appear only once.'; return;
    }
    this.submitting = true;
    this.api.createOrder({ customerId: this.customerId!,
      items: this.lines.map(line => ({ productId: line.productId!, quantity: line.quantity! })) })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.submitting = false)).subscribe({
        next: order => { void this.router.navigate(['/orders', order.id]); },
        error: error => {
          this.message = error instanceof HttpErrorResponse && error.status === 409
            ? 'Insufficient stock. ' + apiErrorMessage(error) + ' Reload products and reduce the quantities before retrying.'
            : apiErrorMessage(error);
        }
      });
  }
}
