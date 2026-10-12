import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { environment } from '../../environments/environment';
import { Customer, LoginResponse, OrderDetails, Product } from '../core/api.models';
import { LoginComponent } from './login.component';
import { ProductsComponent } from './products.component';
import { OrderCreateComponent } from './order-create.component';
import { OrdersComponent } from './orders.component';
import { OrderDetailsComponent } from './order-details.component';

describe('Required Angular pages', () => {
  const base = environment.apiUrl + '/api';
  const customers: Customer[] = [{ id: 4, code: 'CUST-004', name: 'Customer four' }];
  const product: Product = { id: 7, code: 'LAPTOP-001', name: 'Laptop', price: 12.34,
    stockQuantity: 5, isActive: true };
  const detail: OrderDetails = { id: 25, orderNumber: 'historical-order', customerId: 4,
    orderDate: '2026-01-10T12:00:00Z', status: 'Confirmed', totalAmount: 21.23, createdBy: 2,
    customer: customers[0], createdByUser: { id: 2, username: 'sales' },
    items: [{ productId: 7, productCode: 'LAPTOP-001', productName: 'Laptop', quantity: 2,
      unitPrice: 12.34, totalPrice: 21.23 }] };
  let http: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    sessionStorage.removeItem('challenge.auth');
    TestBed.configureTestingModule({ imports: [LoginComponent, ProductsComponent, OrderCreateComponent, OrdersComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        provideRouter([{ path: 'orders/:id', component: OrderDetailsComponent }])] });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
  });
  afterEach(() => { http.verify(); sessionStorage.removeItem('challenge.auth'); });

  function productRequest(): TestRequest { return http.expectOne(req => req.url === base + '/products'); }
  function flushProducts(request: TestRequest, items = [product], totalCount = 1, page = 1): void {
    request.flush({ items, totalCount, page, pageSize: 10 });
  }
  function createDraft(): ComponentFixture<OrderCreateComponent> {
    const fixture = TestBed.createComponent(OrderCreateComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance.loading).toBeTrue();
    http.expectOne(base + '/customers').flush(customers);
    const request = productRequest();
    expect(request.request.params.get('isActive')).toBe('true');
    flushProducts(request);
    fixture.componentInstance.customerId = 4;
    const line = fixture.componentInstance.lines[0];
    line.productId = product.id;
    fixture.componentInstance.selectProduct(line);
    line.quantity = 2;
    return fixture;
  }

  it('login validates blank input without making an API request', () => {
    const fixture = TestBed.createComponent(LoginComponent);
    fixture.detectChanges();
    fixture.componentInstance.form.setValue({ username: '   ', password: '' });
    fixture.componentInstance.submit();
    expect(fixture.componentInstance.form.invalid).toBeTrue();
    http.expectNone(base + '/auth/login');
  });

  it('login submits once, preserves the password, and redirects after success', () => {
    const fixture = TestBed.createComponent(LoginComponent);
    fixture.detectChanges();
    fixture.componentInstance.form.setValue({ username: ' sales ', password: 'Sales123!' });
    fixture.componentInstance.submit();
    fixture.componentInstance.submit();
    expect(fixture.componentInstance.pending).toBeTrue();
    const requests = http.match(base + '/auth/login');
    expect(requests.length).toBe(1);
    expect(requests[0].request.body).toEqual({ username: 'sales', password: 'Sales123!' });
    const response: LoginResponse = { token: 'header.' + btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 60 })) + '.signature',
      expiresAt: new Date(Date.now() + 60_000).toISOString(), user: { id: 2, username: 'sales', role: 'Sales' } };
    requests[0].flush(response);
    expect(fixture.componentInstance.pending).toBeFalse();
    expect(router.navigate).toHaveBeenCalledWith(['/products']);
  });

  it('invalid login displays the backend error and permits retry', () => {
    const fixture = TestBed.createComponent(LoginComponent);
    fixture.detectChanges();
    fixture.componentInstance.form.setValue({ username: 'sales', password: 'wrong' });
    fixture.componentInstance.submit();
    http.expectOne(base + '/auth/login').flush({ message: 'Invalid username or password.' },
      { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Invalid username or password.');
    expect(fixture.componentInstance.pending).toBeFalse();
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('products render loading and empty states and send trimmed search to the server', () => {
    const fixture = TestBed.createComponent(ProductsComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Loading products');
    flushProducts(productRequest(), [], 0);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No products match');
    fixture.componentInstance.search = ' Laptop ';
    fixture.componentInstance.applyFilters();
    const request = productRequest();
    expect(request.request.params.get('search')).toBe('Laptop');
    expect(request.request.params.get('page')).toBe('1');
    flushProducts(request);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('LAPTOP-001');
    expect(fixture.nativeElement.textContent).toContain('12.34');
  });

  it('products paginate on the server and reset the page when filtering inactive products', () => {
    const fixture = TestBed.createComponent(ProductsComponent);
    fixture.detectChanges();
    flushProducts(productRequest(), [product], 23);
    fixture.componentInstance.next();
    const second = productRequest();
    expect(second.request.params.get('page')).toBe('2');
    flushProducts(second, [product], 23, 2);
    fixture.componentInstance.isActive = false;
    fixture.componentInstance.applyFilters();
    const inactive = productRequest();
    expect(inactive.request.params.get('isActive')).toBe('false');
    expect(inactive.request.params.get('page')).toBe('1');
    flushProducts(inactive, [{ ...product, isActive: false }]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Inactive');
    fixture.componentInstance.next();
    http.expectNone(req => req.url === base + '/products');
  });

  it('products display API errors instead of claiming an empty result', () => {
    const fixture = TestBed.createComponent(ProductsComponent);
    fixture.detectChanges();
    productRequest().flush({}, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('permission');
    expect(fixture.nativeElement.textContent).not.toContain('No products match');
  });

  it('create order loads customers, previews totals, submits only IDs/quantities once, and uses the returned ID', () => {
    const fixture = createDraft();
    expect(fixture.componentInstance.total).toBeCloseTo(24.68);
    fixture.componentInstance.submit();
    fixture.componentInstance.submit();
    const requests = http.match(base + '/orders');
    expect(requests.length).toBe(1);
    expect(requests[0].request.body).toEqual({ customerId: 4, items: [{ productId: 7, quantity: 2 }] });
    expect(fixture.componentInstance.submitting).toBeTrue();
    requests[0].flush({ ...detail, id: 88, totalAmount: 99.99 });
    expect(router.navigate).toHaveBeenCalledWith(['/orders', 88]);
    expect(fixture.componentInstance.submitting).toBeFalse();
  });

  for (const quantity of [null, 0, -1, 1.5, Number.NaN, 2_147_483_648]) {
    it('create order rejects an invalid quantity: ' + quantity, () => {
      const fixture = createDraft();
      fixture.componentInstance.lines[0].quantity = quantity;
      fixture.componentInstance.submit();
      expect(fixture.componentInstance.message).toContain('positive whole-number');
      http.expectNone(base + '/orders');
    });
  }

  it('create order validates customer, distinct products, and nonempty items, and supports removing items', () => {
    const fixture = createDraft();
    const component = fixture.componentInstance;
    component.customerId = null;
    component.submit();
    expect(component.message).toContain('Select an active customer');
    component.customerId = 4;
    component.addLine();
    component.lines[1].productId = 7;
    component.selectProduct(component.lines[1]);
    component.submit();
    expect(component.message).toContain('only once');
    expect(component.selectedElsewhere(7, component.lines[1])).toBeTrue();
    for (const line of [...component.lines]) component.removeLine(line);
    component.submit();
    expect(component.message).toContain('at least one product');
    http.expectNone(base + '/orders');
  });

  it('create order preserves selected products while fetching subsequent catalog pages', () => {
    const fixture = createDraft();
    const component = fixture.componentInstance;
    component.productCount = 23;
    component.nextProducts();
    const request = productRequest();
    expect(request.request.params.get('page')).toBe('2');
    flushProducts(request, [{ ...product, id: 9, code: 'OTHER' }], 23, 2);
    expect(component.lines[0].product?.id).toBe(7);
    expect(component.optionsFor(component.lines[0]).some(item => item.id === 7)).toBeTrue();
    expect(component.total).toBeCloseTo(24.68);
  });

  it('create order handles insufficient stock without submitting again automatically', () => {
    const fixture = createDraft();
    fixture.componentInstance.submit();
    http.expectOne(base + '/orders').flush({ code: 'insufficient_stock', title: 'Product 7 has insufficient stock.' },
      { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Insufficient stock');
    expect(fixture.componentInstance.submitting).toBeFalse();
    expect(router.navigate).not.toHaveBeenCalled();
    http.expectNone(base + '/orders');
  });

  it('create order displays backend validation errors', () => {
    const fixture = createDraft();
    fixture.componentInstance.submit();
    http.expectOne(base + '/orders').flush({ errors: { customerId: ['Customer is inactive.'] } },
      { status: 400, statusText: 'Bad Request' });
    expect(fixture.componentInstance.message).toBe('Customer is inactive.');
  });

  it('orders show loading/empty states and send every documented filter and sort parameter', () => {
    const fixture = TestBed.createComponent(OrdersComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Loading orders');
    http.expectOne(req => req.url === base + '/orders').flush({ items: [], totalCount: 0, page: 1, pageSize: 10 });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No orders match');
    const component = fixture.componentInstance;
    Object.assign(component, { orderNumber: ' historical ', customerId: 4, status: 'Confirmed',
      fromDate: '2026-01-10', toDate: '2026-01-10', sortBy: 'totalAmount', sortDirection: 'asc' });
    component.applyFilters();
    const request = http.expectOne(req => req.url === base + '/orders');
    expect(Object.fromEntries(request.request.params.keys().map(key => [key, request.request.params.get(key)])))
      .toEqual({ orderNumber: 'historical', customerId: '4', status: 'Confirmed', fromDate: '2026-01-10',
        toDate: '2026-01-10', sortBy: 'totalAmount', sortDirection: 'asc', page: '1', pageSize: '10' });
    request.flush({ items: [{ ...detail, customerName: 'Customer four' }], totalCount: 1, page: 1, pageSize: 10 });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('historical-order');
    expect(fixture.nativeElement.querySelector('tbody a').getAttribute('href')).toBe('/orders/25');
  });

  it('orders paginate using SQL-backed API pages', () => {
    const fixture = TestBed.createComponent(OrdersComponent);
    fixture.detectChanges();
    http.expectOne(req => req.url === base + '/orders').flush({ items: [], totalCount: 23, page: 1, pageSize: 10 });
    fixture.componentInstance.next();
    const request = http.expectOne(req => req.url === base + '/orders');
    expect(request.request.params.get('page')).toBe('2');
    request.flush({ items: [], totalCount: 23, page: 2, pageSize: 10 });
    expect(fixture.componentInstance.page).toBe(2);
  });

  it('orders reject reversed dates and invalid customer IDs without sending a request', () => {
    const fixture = TestBed.createComponent(OrdersComponent);
    fixture.detectChanges();
    http.expectOne(req => req.url === base + '/orders').flush({ items: [], totalCount: 0, page: 1, pageSize: 10 });
    Object.assign(fixture.componentInstance, { fromDate: '2026-01-12', toDate: '2026-01-10' });
    fixture.componentInstance.applyFilters();
    expect(fixture.componentInstance.error).toContain('on or before');
    Object.assign(fixture.componentInstance, { fromDate: '', toDate: '', customerId: -1 });
    fixture.componentInstance.applyFilters();
    expect(fixture.componentInstance.error).toContain('positive integer');
    http.expectNone(req => req.url === base + '/orders');
  });

  it('details render historical amounts and creator without querying current product prices', async () => {
    const harness = await RouterTestingHarness.create('/orders/25');
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Loading order');
    http.expectOne(base + '/orders/25').flush(detail);
    harness.detectChanges();
    const content = harness.routeNativeElement!.textContent!;
    expect(content).toContain('sales');
    expect(content).toContain('Customer four');
    expect(content).toContain('12.34');
    expect(content).toContain('21.23');
    expect(content).not.toContain('24.68');
    http.expectNone(req => req.url === base + '/products');
  });

  it('details handle a missing order and then load a changed route ID', async () => {
    const harness = await RouterTestingHarness.create('/orders/25');
    http.expectOne(base + '/orders/25').flush({ code: 'order_not_found', title: 'Order was not found.' },
      { status: 404, statusText: 'Not Found' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Order was not found');
    await harness.navigateByUrl('/orders/26');
    http.expectOne(base + '/orders/26').flush({ ...detail, id: 26, orderNumber: 'second-order' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('second-order');
    expect(harness.routeNativeElement?.textContent).not.toContain('Order was not found');
  });

  it('details reject an invalid route ID without an API request', async () => {
    const harness = await RouterTestingHarness.create('/orders/abc');
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Invalid order ID');
    http.expectNone(req => req.url.startsWith(base + '/orders/'));
  });
});
