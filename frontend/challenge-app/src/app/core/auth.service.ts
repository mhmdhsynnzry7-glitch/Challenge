import { HttpClient } from '@angular/common/http';
import { inject, Injectable, OnDestroy, signal } from '@angular/core';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { LoginResponse, LoginUser } from './api.models';

@Injectable({ providedIn: 'root' })
export class AuthService implements OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly storageKey = 'challenge.auth';
  private session: LoginResponse | null = null;
  private expiresAt = 0;
  private expiryTimer?: ReturnType<typeof setTimeout>;
  private readonly currentUser = signal<LoginUser | null>(null);
  readonly user = this.currentUser.asReadonly();

  constructor() {
    try {
      const saved = sessionStorage.getItem(this.storageKey);
      if (saved) this.setSession(JSON.parse(saved));
    } catch { this.clearSession(); }
  }

  get token(): string | null {
    if (this.session && Date.now() >= this.expiresAt) this.clearSession();
    return this.session?.token ?? null;
  }

  login(credentials: { username: string; password: string }) {
    return this.http.post<LoginResponse>(environment.apiUrl + '/api/auth/login', credentials)
      .pipe(tap(response => this.setSession(response)));
  }

  logout(): void {
    this.clearSession();
    void this.router.navigate(['/login']);
  }

  private setSession(response: LoginResponse): void {
    const payload = JSON.parse(atob(response.token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
    const expiry = Math.min(Date.parse(response.expiresAt), Number(payload.exp) * 1000);
    if (!response.token || !Number.isFinite(expiry) || expiry <= Date.now() ||
        !Number.isInteger(response.user?.id) || response.user.id < 1 || !response.user.username ||
        !['Admin', 'Sales'].includes(response.user.role)) {
      throw new Error('Invalid authentication response. Please log in again.');
    }
    this.clearSession();
    this.session = response;
    this.expiresAt = expiry;
    this.currentUser.set(response.user);
    try { sessionStorage.setItem(this.storageKey, JSON.stringify(response)); } catch { /* Use memory if unavailable. */ }
    this.expiryTimer = setTimeout(() => this.logout(), Math.min(expiry - Date.now(), 2_147_483_647));
  }

  private clearSession(): void {
    clearTimeout(this.expiryTimer);
    this.session = null;
    this.expiresAt = 0;
    this.currentUser.set(null);
    try { sessionStorage.removeItem(this.storageKey); } catch { /* Storage may be unavailable. */ }
  }
  ngOnDestroy(): void { clearTimeout(this.expiryTimer); }
}
