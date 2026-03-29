import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable, firstValueFrom } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../../environments/environment';

interface TokenResponse {
  access_token: string;
  id_token: string;
  token_type: string;
  expires_in: number;
  refresh_token?: string;
}

interface OidcDiscovery {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

interface IdTokenClaims {
  sub: string;
  email?: string;
  name?: string;
  given_name?: string;
  [key: string]: any;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private isAuthenticatedSubject = new BehaviorSubject<boolean>(false);
  public isAuthenticated$: Observable<boolean> = this.isAuthenticatedSubject.asObservable();

  private discoveryDoc: OidcDiscovery | null = null;
  private idTokenClaims: IdTokenClaims | null = null;
  private refreshTokenPromise: Promise<boolean> | null = null;

  private readonly STORAGE_ACCESS_TOKEN = 'auth_access_token';
  private readonly STORAGE_ID_TOKEN = 'auth_id_token';
  private readonly STORAGE_REFRESH_TOKEN = 'auth_refresh_token';
  private readonly STORAGE_TOKEN_EXPIRY = 'auth_token_expiry';
  private readonly STORAGE_CODE_VERIFIER = 'auth_code_verifier';
  private readonly STORAGE_STATE = 'auth_state';

  get authEnabled(): boolean {
    return !!environment.oidc.authority;
  }

  constructor(private http: HttpClient) {
    this.checkAuthState();
    if (this.authEnabled) {
      this.loadDiscoveryDocument();
    }
  }

  private async loadDiscoveryDocument(): Promise<void> {
    try {
      const url = `${environment.oidc.authority}/.well-known/openid-configuration`;
      this.discoveryDoc = await firstValueFrom(this.http.get<OidcDiscovery>(url));
    } catch (error) {
      console.error('[AUTH] Failed to load discovery document:', error);
    }
  }

  private checkAuthState(): void {
    if (!this.authEnabled) {
      this.isAuthenticatedSubject.next(true);
      return;
    }

    const token = localStorage.getItem(this.STORAGE_ACCESS_TOKEN);
    const expiry = localStorage.getItem(this.STORAGE_TOKEN_EXPIRY);

    if (token && expiry) {
      const expiryTime = parseInt(expiry, 10);
      if (Date.now() < expiryTime) {
        this.isAuthenticatedSubject.next(true);
        this.parseIdToken();
      } else {
        this.clearSession();
      }
    }
  }

  private parseIdToken(): void {
    const idToken = localStorage.getItem(this.STORAGE_ID_TOKEN);
    if (!idToken) return;

    try {
      const payload = idToken.split('.')[1];
      let base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
      while (base64.length % 4) base64 += '=';
      this.idTokenClaims = JSON.parse(decodeURIComponent(escape(atob(base64))));
    } catch {
      // ignore parse errors
    }
  }

  async ensureInitialized(): Promise<void> {
    if (this.authEnabled && !this.discoveryDoc) {
      await this.loadDiscoveryDocument();
    }
  }

  async signIn(): Promise<void> {
    if (!this.authEnabled) return;

    if (!this.discoveryDoc) {
      await this.loadDiscoveryDocument();
      if (!this.discoveryDoc) throw new Error('Discovery document not loaded');
    }

    const codeVerifier = this.generateRandomString(64);
    const codeChallenge = await this.generateCodeChallenge(codeVerifier);
    const state = this.generateRandomString(16);

    localStorage.setItem(this.STORAGE_CODE_VERIFIER, codeVerifier);
    localStorage.setItem(this.STORAGE_STATE, state);

    const authUrl = new URL(this.discoveryDoc.authorization_endpoint);
    authUrl.searchParams.set('client_id', environment.oidc.clientId);
    authUrl.searchParams.set('redirect_uri', environment.oidc.redirectUrl);
    authUrl.searchParams.set('response_type', 'code');
    authUrl.searchParams.set('scope', environment.oidc.scope);
    authUrl.searchParams.set('code_challenge', codeChallenge);
    authUrl.searchParams.set('code_challenge_method', 'S256');
    authUrl.searchParams.set('state', state);

    console.log('[AUTH] Redirecting to:', authUrl.toString());
    window.location.href = authUrl.toString();
  }

  async handleCallback(code: string, state?: string): Promise<boolean> {
    if (!this.discoveryDoc) {
      await this.loadDiscoveryDocument();
      if (!this.discoveryDoc) return false;
    }

    const storedState = localStorage.getItem(this.STORAGE_STATE);
    if (storedState && state !== storedState) {
      localStorage.removeItem(this.STORAGE_STATE);
      return false;
    }
    localStorage.removeItem(this.STORAGE_STATE);

    const codeVerifier = localStorage.getItem(this.STORAGE_CODE_VERIFIER);
    if (!codeVerifier) return false;

    try {
      const tokenParams = new URLSearchParams({
        grant_type: 'authorization_code',
        code,
        redirect_uri: environment.oidc.redirectUrl,
        client_id: environment.oidc.clientId,
        code_verifier: codeVerifier,
      });

      const response = await firstValueFrom(this.http.post<TokenResponse>(
        this.discoveryDoc.token_endpoint,
        tokenParams.toString(),
        { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }
      ));

      localStorage.setItem(this.STORAGE_ACCESS_TOKEN, response.access_token);
      localStorage.setItem(this.STORAGE_ID_TOKEN, response.id_token);
      if (response.refresh_token) {
        localStorage.setItem(this.STORAGE_REFRESH_TOKEN, response.refresh_token);
      }
      localStorage.setItem(this.STORAGE_TOKEN_EXPIRY, (Date.now() + response.expires_in * 1000).toString());
      localStorage.removeItem(this.STORAGE_CODE_VERIFIER);

      this.parseIdToken();
      this.isAuthenticatedSubject.next(true);
      return true;
    } catch (error) {
      console.error('[AUTH] Token exchange failed:', error);
      this.clearSession();
      return false;
    }
  }

  async getToken(): Promise<string | null> {
    if (!this.authEnabled) return null;

    const token = localStorage.getItem(this.STORAGE_ACCESS_TOKEN);
    const expiry = localStorage.getItem(this.STORAGE_TOKEN_EXPIRY);
    if (!token || !expiry) return null;

    if (Date.now() >= parseInt(expiry, 10)) {
      const refreshed = await this.refreshToken();
      return refreshed ? localStorage.getItem(this.STORAGE_ACCESS_TOKEN) : null;
    }

    return token;
  }

  private async refreshToken(): Promise<boolean> {
    if (this.refreshTokenPromise) return this.refreshTokenPromise;

    this.refreshTokenPromise = this.doRefreshToken();
    try {
      return await this.refreshTokenPromise;
    } finally {
      this.refreshTokenPromise = null;
    }
  }

  private async doRefreshToken(): Promise<boolean> {
    const refreshToken = localStorage.getItem(this.STORAGE_REFRESH_TOKEN);
    if (!refreshToken) { this.clearSession(); return false; }

    if (!this.discoveryDoc) {
      await this.loadDiscoveryDocument();
      if (!this.discoveryDoc) return false;
    }

    try {
      const tokenParams = new URLSearchParams({
        grant_type: 'refresh_token',
        refresh_token: refreshToken,
        client_id: environment.oidc.clientId,
      });

      const response = await firstValueFrom(this.http.post<TokenResponse>(
        this.discoveryDoc.token_endpoint,
        tokenParams.toString(),
        { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }
      ));

      localStorage.setItem(this.STORAGE_ACCESS_TOKEN, response.access_token);
      localStorage.setItem(this.STORAGE_ID_TOKEN, response.id_token);
      if (response.refresh_token) {
        localStorage.setItem(this.STORAGE_REFRESH_TOKEN, response.refresh_token);
      }
      localStorage.setItem(this.STORAGE_TOKEN_EXPIRY, (Date.now() + response.expires_in * 1000).toString());

      this.parseIdToken();
      return true;
    } catch {
      this.clearSession();
      return false;
    }
  }

  isAuthenticated(): boolean {
    if (!this.authEnabled) return true;
    return this.isAuthenticatedSubject.value;
  }

  async signOut(): Promise<void> {
    const idToken = localStorage.getItem(this.STORAGE_ID_TOKEN);
    this.clearSession();

    // Clear Andy.Auth session via iframe (non-blocking), then redirect to login
    if (idToken && this.authEnabled) {
      const endSessionEndpoint = `${environment.oidc.authority}/connect/logout`;
      const logoutUrl = `${endSessionEndpoint}?id_token_hint=${encodeURIComponent(idToken)}`;
      const iframe = document.createElement('iframe');
      iframe.style.display = 'none';
      iframe.src = logoutUrl;
      document.body.appendChild(iframe);
      // Give the iframe a moment to clear the cookie
      await new Promise(resolve => setTimeout(resolve, 500));
      document.body.removeChild(iframe);
    }

    window.location.href = '/login';
  }

  private clearSession(): void {
    localStorage.removeItem(this.STORAGE_ACCESS_TOKEN);
    localStorage.removeItem(this.STORAGE_ID_TOKEN);
    localStorage.removeItem(this.STORAGE_REFRESH_TOKEN);
    localStorage.removeItem(this.STORAGE_TOKEN_EXPIRY);
    localStorage.removeItem(this.STORAGE_CODE_VERIFIER);
    localStorage.removeItem(this.STORAGE_STATE);
    this.idTokenClaims = null;
    this.isAuthenticatedSubject.next(false);
  }

  getUserName(): string | null {
    return this.idTokenClaims?.name ?? this.idTokenClaims?.given_name ?? this.idTokenClaims?.email ?? null;
  }

  getUserEmail(): string | null {
    return this.idTokenClaims?.email ?? null;
  }

  getUserId(): string | null {
    return this.idTokenClaims?.sub ?? null;
  }

  private generateRandomString(length: number): string {
    const array = new Uint8Array(length);
    crypto.getRandomValues(array);
    return Array.from(array, b => b.toString(16).padStart(2, '0')).join('');
  }

  private async generateCodeChallenge(verifier: string): Promise<string> {
    const data = new TextEncoder().encode(verifier);
    const hash = await crypto.subtle.digest('SHA-256', data);
    return btoa(String.fromCharCode(...new Uint8Array(hash)))
      .replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
  }
}
