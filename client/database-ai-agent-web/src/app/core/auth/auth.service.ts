import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CurrentUser, InvitationPreview, MessageResponse, Permission, SessionResponse } from './auth.models';

/** Refresh this long before the access token expires. */
const REFRESH_LEAD_MS = 60_000;
const REFRESH_CONFLICT_RETRY_MS = 750;
const REFRESH_LOCK = 'database-ai-agent-refresh';

/**
 * Session state for the SPA. The access token is held in memory only (never in storage); the refresh token is an
 * httpOnly cookie the browser sends to /api/auth automatically. Refreshes are single-flight within a tab and
 * serialized across tabs with the Web Locks API, so concurrent tabs never trip refresh-token reuse detection.
 */
@Injectable({ providedIn: 'root' })
export class AuthService implements OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly currentUser = signal<CurrentUser | null>(null);
  private accessToken: string | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | undefined;
  private refreshInFlight: Promise<boolean> | null = null;

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser() !== null);

  ngOnDestroy(): void {
    clearTimeout(this.refreshTimer);
  }

  getAccessToken(): string | null {
    return this.accessToken;
  }

  hasPermission(permission: Permission): boolean {
    return this.currentUser()?.permissions.includes(permission) ?? false;
  }

  async login(email: string, password: string): Promise<void> {
    this.setSession(await firstValueFrom(this.http.post<SessionResponse>('/api/auth/login', { email, password })));
  }

  register(displayName: string, email: string, password: string): Promise<MessageResponse> {
    return firstValueFrom(this.http.post<MessageResponse>('/api/auth/register', { displayName, email, password }));
  }

  verifyEmail(userId: string, token: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/verify-email', { userId, token }));
  }

  resendVerification(email: string): Promise<MessageResponse> {
    return firstValueFrom(this.http.post<MessageResponse>('/api/auth/resend-verification', { email }));
  }

  forgotPassword(email: string): Promise<MessageResponse> {
    return firstValueFrom(this.http.post<MessageResponse>('/api/auth/forgot-password', { email }));
  }

  resetPassword(email: string, token: string, newPassword: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/reset-password', { email, token, newPassword }));
  }

  previewInvitation(token: string): Promise<InvitationPreview> {
    return firstValueFrom(this.http.post<InvitationPreview>('/api/invitations/preview', { token }));
  }

  async acceptInvitation(token: string, displayName: string, password: string): Promise<void> {
    this.setSession(
      await firstValueFrom(this.http.post<SessionResponse>('/api/invitations/accept', { token, displayName, password })),
    );
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    this.setSession(
      await firstValueFrom(
        this.http.post<SessionResponse>('/api/account/change-password', { currentPassword, newPassword }),
      ),
    );
  }

  async updateProfile(displayName: string): Promise<void> {
    this.currentUser.set(await firstValueFrom(this.http.put<CurrentUser>('/api/account/profile', { displayName })));
  }

  /** Re-reads the current user (role/permissions may have been changed by an administrator). */
  async reloadUser(): Promise<void> {
    this.currentUser.set(await firstValueFrom(this.http.get<CurrentUser>('/api/account/me')));
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>('/api/auth/logout', {}));
    } catch {
      // Signing out locally must succeed even if the server is unreachable.
    } finally {
      this.clearSession();
      await this.router.navigateByUrl('/login');
    }
  }

  async logoutEverywhere(): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>('/api/account/logout-everywhere', {}));
    } finally {
      this.clearSession();
      await this.router.navigateByUrl('/login');
    }
  }

  /** Restores a session from the refresh cookie at startup. Never throws. */
  async restoreSession(): Promise<void> {
    await this.refresh();
  }

  /** Obtains a new access token. Resolves false (and clears the session) when the session cannot be renewed. */
  refresh(): Promise<boolean> {
    this.refreshInFlight ??= this.withCrossTabLock(() => this.refreshOnce(true)).finally(() => {
      this.refreshInFlight = null;
    });
    return this.refreshInFlight;
  }

  /** Called when an authenticated request fails and the session cannot be renewed. */
  async handleSessionExpired(): Promise<void> {
    this.clearSession();
    const returnUrl = this.router.url.startsWith('/app') ? this.router.url : undefined;
    await this.router.navigate(['/login'], { queryParams: returnUrl ? { returnUrl, expired: 1 } : { expired: 1 } });
  }

  private async refreshOnce(retryOnConflict: boolean): Promise<boolean> {
    try {
      this.setSession(await firstValueFrom(this.http.post<SessionResponse>('/api/auth/refresh', {})));
      return true;
    } catch (error) {
      if (retryOnConflict && error instanceof HttpErrorResponse && error.status === 409) {
        // Another tab rotated the token a moment ago; its new cookie is now in place.
        await new Promise((resolve) => setTimeout(resolve, REFRESH_CONFLICT_RETRY_MS));
        return this.refreshOnce(false);
      }

      if (error instanceof HttpErrorResponse && (error.status === 401 || error.status === 400)) {
        this.clearSession();
      }
      return false;
    }
  }

  private withCrossTabLock<T>(work: () => Promise<T>): Promise<T> {
    const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined;
    return locks ? locks.request(REFRESH_LOCK, work) : work();
  }

  private setSession(session: SessionResponse): void {
    this.accessToken = session.accessToken;
    this.currentUser.set(session.user);

    clearTimeout(this.refreshTimer);
    const delay = new Date(session.accessTokenExpiresAt).getTime() - Date.now() - REFRESH_LEAD_MS;
    this.refreshTimer = setTimeout(() => void this.refresh(), Math.max(delay, 5_000));
  }

  private clearSession(): void {
    clearTimeout(this.refreshTimer);
    this.accessToken = null;
    this.currentUser.set(null);
  }
}
