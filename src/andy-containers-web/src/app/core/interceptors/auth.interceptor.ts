import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError, from, switchMap } from 'rxjs';
import { AuthService } from '../services/auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.includes('/api')) {
    return next(req);
  }

  const authService = inject(AuthService);
  const router = inject(Router);

  if (!authService.authEnabled) {
    return next(req);
  }

  return from(
    authService.ensureInitialized().then(() => authService.getToken())
  ).pipe(
    switchMap(token => {
      if (token) {
        req = req.clone({
          setHeaders: { Authorization: `Bearer ${token}` }
        });
      }

      return next(req).pipe(
        catchError((error: HttpErrorResponse) => {
          if (error.status === 401) {
            authService.signOut();
          }
          if (error.status === 403) {
            const enrichedError = new HttpErrorResponse({
              error: {
                error: 'Access denied. You do not have the required permissions for this operation. Contact your administrator to request access.',
                code: 'FORBIDDEN',
                originalError: error.error
              },
              headers: error.headers,
              status: 403,
              statusText: 'Forbidden',
              url: error.url ?? undefined
            });
            return throwError(() => enrichedError);
          }
          return throwError(() => error);
        })
      );
    })
  );
};
