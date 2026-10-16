import { SessionResponse } from '../core/auth/auth.models';

/** A session as returned by /api/auth/login, valid for ten minutes. */
export function session(token = 'access-1', permissions: SessionResponse['user']['permissions'] = ['chat.use']): SessionResponse {
  return {
    accessToken: token,
    accessTokenExpiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
    user: { id: 'u1', email: 'ava@example.test', displayName: 'Ava', role: 'Analyst', permissions },
  };
}
