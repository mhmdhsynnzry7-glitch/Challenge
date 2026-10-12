import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(environment.apiUrl + '/api/') ||
      req.url === environment.apiUrl + '/api/auth/login') return next(req);
  const auth = inject(AuthService);
  const token = auth.token;
  return next(token ? req.clone({ setHeaders: { Authorization: 'Bearer ' + token } }) : req).pipe(
    catchError(error => {
      // A late response from an old session must not log out a newly authenticated user.
      if (error instanceof HttpErrorResponse && error.status === 401 && auth.token === token) auth.logout();
      return throwError(() => error);
    })
  );
};
