import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Permission, Role } from '../auth/auth.models';

export interface TeamMember {
  id: string;
  email: string;
  displayName: string;
  role: Role;
  isActive: boolean;
  emailConfirmed: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface Invitation {
  id: string;
  email: string;
  role: Role;
  createdAt: string;
  expiresAt: string;
}

export interface RoleInfo {
  name: Role;
  permissions: Permission[];
  memberCount: number;
  editable: boolean;
}

@Injectable({ providedIn: 'root' })
export class TeamService {
  private readonly http = inject(HttpClient);

  members(): Promise<TeamMember[]> {
    return firstValueFrom(this.http.get<TeamMember[]>('/api/team/members'));
  }

  invitations(): Promise<Invitation[]> {
    return firstValueFrom(this.http.get<Invitation[]>('/api/team/invitations'));
  }

  invite(email: string, role: Role): Promise<Invitation> {
    return firstValueFrom(this.http.post<Invitation>('/api/team/invitations', { email, role }));
  }

  revokeInvitation(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/team/invitations/${encodeURIComponent(id)}`));
  }

  changeRole(userId: string, role: Role): Promise<TeamMember> {
    return firstValueFrom(this.http.put<TeamMember>(`/api/team/members/${encodeURIComponent(userId)}/role`, { role }));
  }

  setActive(userId: string, active: boolean): Promise<TeamMember> {
    const action = active ? 'reactivate' : 'deactivate';
    return firstValueFrom(this.http.post<TeamMember>(`/api/team/members/${encodeURIComponent(userId)}/${action}`, {}));
  }

  roles(): Promise<RoleInfo[]> {
    return firstValueFrom(this.http.get<RoleInfo[]>('/api/roles'));
  }

  updateRolePermissions(role: Role, permissions: Permission[]): Promise<RoleInfo> {
    return firstValueFrom(
      this.http.put<RoleInfo>(`/api/roles/${encodeURIComponent(role)}/permissions`, { permissions }),
    );
  }
}
