import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

export const authGuard: CanActivateFn = async (route, state) => {
  const authService = inject(AuthService);
  const router = inject(Router);

  if (!authService.authEnabled) return true;

  await authService.ensureInitialized();

  if (authService.isAuthenticated()) return true;

  localStorage.setItem('auth_return_url', state.url);
  router.navigate(['/login']);
  return false;
};
