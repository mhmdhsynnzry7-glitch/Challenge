import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  template: `
    <header>
      <strong>Sales Challenge</strong>
      <nav>
        @if (!auth.user()) { <a routerLink="/login">Login</a> }
        <a routerLink="/products">Products</a>
        <a routerLink="/orders">Orders</a>
        <a routerLink="/orders/create">Create order</a>
        @if (auth.user(); as user) {
          <span>{{ user.username }}</span>
          <button type="button" (click)="auth.logout()">Logout</button>
        }
      </nav>
    </header>
    <main>
      <router-outlet />
    </main>
  `
})
export class AppComponent { readonly auth = inject(AuthService); }
