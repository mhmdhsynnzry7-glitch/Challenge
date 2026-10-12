import { Component, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { finalize } from 'rxjs';
import { apiErrorMessage } from '../core/api-error';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  template: `
    <h1>Login</h1>
    <form [formGroup]="form" (ngSubmit)="submit()">
      <label>
        Username
        <input formControlName="username" autocomplete="username" maxlength="64" />
      </label>
      <label>
        Password
        <input type="password" formControlName="password" autocomplete="current-password" />
      </label>
      @if (form.invalid && form.touched) { <p role="alert">Enter a username and password.</p> }
      <button type="submit" [disabled]="pending">{{ pending ? 'Logging in…' : 'Login' }}</button>
    </form>
    @if (message) {
      <p role="alert">{{ message }}</p>
    }
  `
})
export class LoginComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  readonly form = this.fb.nonNullable.group({
    username: ['', [Validators.required, Validators.maxLength(64)]],
    password: ['', Validators.required]
  });
  message = '';
  pending = false;

  ngOnInit(): void { if (this.auth.token) void this.router.navigate(['/products']); }

  submit(): void {
    if (this.pending) return;
    this.message = '';
    this.form.markAllAsTouched();
    const credentials = this.form.getRawValue();
    credentials.username = credentials.username.trim();
    if (!credentials.username) this.form.controls.username.setErrors({ required: true });
    if (!credentials.password.trim()) this.form.controls.password.setErrors({ required: true });
    if (this.form.invalid) return;
    this.pending = true;
    this.auth.login(credentials).pipe(takeUntilDestroyed(this.destroyRef),
      finalize(() => this.pending = false)).subscribe({
      next: () => { void this.router.navigate(['/products']); },
      error: error => { this.message = apiErrorMessage(error); }
    });
  }
}
