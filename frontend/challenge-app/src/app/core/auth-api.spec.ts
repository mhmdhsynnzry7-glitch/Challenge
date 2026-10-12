import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { environment } from '../../environments/environment';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { authGuard } from './auth.guard';
import { authInterceptor } from './auth.interceptor';
import { LoginResponse } from './api.models';
import { apiErrorMessage } from './api-error';
import { HttpErrorResponse } from '@angular/common/http';

describe('Authentication and API integration', () => {
  const base = environment.apiUrl + '/api';
  let http: HttpTestingController;
  let router: Router;
  let api: ApiService;

  function session(milliseconds = 60_000, nonce = 'test'): LoginResponse {
    return { token: 'header.' + btoa(JSON.stringify({ exp: Math.floor((Date.now() + milliseconds) / 1000), nonce })) + '.signature',
      expiresAt: new Date(Date.now() + milliseconds).toISOString(), user: { id: 2, username: 'sales', role: 'Sales' } };
  }
  function login(response = session()): AuthService {
    const auth = TestBed.inject(AuthService);
    auth.login({ username: 'sales', password: 'Sales123!' }).subscribe();
    const request = http.expectOne(base + '/auth/login');
    expect(request.request.body).toEqual({ username: 'sales', password: 'Sales123!' });
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush(response);
    return auth;
  }

  beforeEach(() => {
    sessionStorage.removeItem('challenge.auth');
    TestBed.configureTestingModule({ providers: [provideRouter([]),
      provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(ApiService);
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
  });
  afterEach(() => { http.verify(); sessionStorage.removeItem('challenge.auth'); });

  it('stores the login response without storing credentials', () => {
    const response = session();
    const auth = login(response);
    expect(auth.token).toBe(response.token);
    expect(auth.user()).toEqual(response.user);
    const stored = sessionStorage.getItem('challenge.auth')!;
    expect(JSON.parse(stored)).toEqual(response);
    expect(stored).not.toContain('Sales123!');
    expect(stored).not.toContain('password');
  });

  it('restores an unexpired session after a reload', () => {
    const saved = session();
    sessionStorage.setItem('challenge.auth', JSON.stringify(saved));
    expect(TestBed.inject(AuthService).token).toBe(saved.token);
    expect(TestBed.inject(AuthService).user()?.username).toBe('sales');
  });

  for (const saved of ['invalid JSON', JSON.stringify({ token: 'bad-token' })]) {
    it('clears a malformed stored session: ' + saved, () => {
      sessionStorage.setItem('challenge.auth', saved);
      expect(TestBed.inject(AuthService).token).toBeNull();
      expect(sessionStorage.getItem('challenge.auth')).toBeNull();
    });
  }

  it('rejects an expired saved session and redirects protected routes to login', () => {
    sessionStorage.setItem('challenge.auth', JSON.stringify(session(-10_000)));
    const result = TestBed.runInInjectionContext(() => authGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(result instanceof UrlTree).toBeTrue();
    expect((result as UrlTree).toString()).toBe('/login');
    expect(sessionStorage.getItem('challenge.auth')).toBeNull();
  });

  it('allows a protected route with an unexpired session', () => {
    login();
    expect(TestBed.runInInjectionContext(() => authGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBeTrue();
  });

  it('expires at JWT exp even if expiresAt has later fractional seconds', fakeAsync(() => {
    const response = session(2_000);
    response.expiresAt = new Date(Date.now() + 10_000).toISOString();
    const auth = login(response);
    tick(2_001);
    expect(auth.token).toBeNull();
    expect(auth.user()).toBeNull();
    expect(sessionStorage.getItem('challenge.auth')).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  }));

  it('logs out and removes the stored session', () => {
    const auth = login();
    auth.logout();
    expect(auth.token).toBeNull();
    expect(auth.user()).toBeNull();
    expect(sessionStorage.getItem('challenge.auth')).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('sends JWT only to the configured API and preserves false query parameters', () => {
    const auth = login();
    api.products({ page: 2, pageSize: 10, search: 'Laptop', isActive: false }).subscribe();
    const request = http.expectOne(req => req.url === base + '/products');
    expect(request.request.headers.get('Authorization')).toBe('Bearer ' + auth.token);
    expect(request.request.params.get('isActive')).toBe('false');
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.get('search')).toBe('Laptop');
    request.flush({ items: [], totalCount: 0, page: 2, pageSize: 10 });
    const client = TestBed.inject(HttpClient);
    client.get('https://example.invalid/api/products').subscribe();
    const external = http.expectOne('https://example.invalid/api/products');
    expect(external.request.headers.has('Authorization')).toBeFalse();
    external.flush([]);
  });

  it('clears authentication and redirects on a protected API 401', () => {
    const auth = login();
    api.customers().subscribe({ error: () => {} });
    http.expectOne(base + '/customers').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(auth.token).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('does not clear authentication on a forbidden response', () => {
    const auth = login();
    const token = auth.token;
    api.customers().subscribe({ error: () => {} });
    http.expectOne(base + '/customers').flush({}, { status: 403, statusText: 'Forbidden' });
    expect(auth.token).toBe(token);
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('does not redirect an invalid login as though it were an expired session', () => {
    TestBed.inject(AuthService).login({ username: 'sales', password: 'wrong' }).subscribe({ error: () => {} });
    http.expectOne(base + '/auth/login').flush({ message: 'Invalid username or password.' },
      { status: 401, statusText: 'Unauthorized' });
    expect(router.navigate).not.toHaveBeenCalled();
    expect(TestBed.inject(AuthService).token).toBeNull();
  });

  it('does not log out a new session because an earlier request returns 401', () => {
    login(session(60_000, 'old'));
    api.customers().subscribe({ error: () => {} });
    const oldRequest = http.expectOne(base + '/customers');
    const replacement = session(60_000, 'new');
    login(replacement);
    oldRequest.flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(TestBed.inject(AuthService).token).toBe(replacement.token);
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('projects only the allowed fields into an order POST', () => {
    const draft = { customerId: 4, totalAmount: 999, status: 'Cancelled', createdBy: 1,
      items: [{ productId: 7, quantity: 2, unitPrice: 999, totalPrice: 1998 }] };
    api.createOrder(draft).subscribe();
    const request = http.expectOne(base + '/orders');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ customerId: 4, items: [{ productId: 7, quantity: 2 }] });
    request.flush({ id: 25 });
  });

  it('formats validation errors and connectivity failures without internal details', () => {
    expect(apiErrorMessage(new HttpErrorResponse({ status: 400,
      error: { errors: { quantity: ['Quantity must be positive.'] }, detail: 'Internal stack trace' } })))
      .toBe('Quantity must be positive.');
    expect(apiErrorMessage(new HttpErrorResponse({ status: 0 }))).toContain('Cannot reach the API');
    expect(apiErrorMessage(new HttpErrorResponse({ status: 403 }))).toContain('permission');
  });
});

