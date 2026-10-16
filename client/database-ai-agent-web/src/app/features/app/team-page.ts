import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { PERMISSIONS, PERMISSION_LABELS, Permission, ROLES, Role } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { toApiProblem } from '../../core/http/api-problem';
import { Invitation, RoleInfo, TeamMember, TeamService } from '../../core/team/team.service';
import { FormError } from '../../shared/form-error';

const RANK: Record<Role, number> = { Owner: 0, Admin: 1, Manager: 2, Analyst: 3, Viewer: 4 };

@Component({
  selector: 'app-team-page',
  imports: [
    DatePipe, ReactiveFormsModule, MatTabsModule, MatTableModule, MatSelectModule, MatButtonModule, MatCardModule,
    MatFormFieldModule, MatInputModule, MatCheckboxModule, MatProgressBarModule, FormError,
  ],
  templateUrl: './team-page.html',
  styleUrl: './team-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TeamPage implements OnInit {
  private readonly team = inject(TeamService);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly auth = inject(AuthService);

  protected readonly permissions = PERMISSIONS;
  protected readonly permissionLabels = PERMISSION_LABELS;
  protected readonly memberColumns = ['name', 'role', 'status', 'lastLogin', 'actions'];

  protected readonly members = signal<TeamMember[]>([]);
  protected readonly invitations = signal<Invitation[]>([]);
  protected readonly roles = signal<RoleInfo[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly roleDrafts = signal<Partial<Record<Role, Permission[]>>>({});

  protected readonly canManage = computed(() => this.auth.hasPermission('users.manage'));
  /** Roles the current user may assign (at or below their own); Owner is never granted by invitation. */
  protected readonly assignableRoles = computed(() => {
    const mine = this.auth.user()?.role ?? 'Viewer';
    return ROLES.filter((role) => RANK[role] >= RANK[mine]);
  });
  protected readonly invitableRoles = computed(() => this.assignableRoles().filter((r) => r !== 'Owner'));

  protected readonly inviteForm = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    role: ['Viewer' as Role, Validators.required],
  });
  protected readonly inviting = signal(false);

  async ngOnInit(): Promise<void> {
    await this.reload();
  }

  protected isSelf(member: TeamMember): boolean {
    return member.id === this.auth.user()?.id;
  }

  protected canManageMember(member: TeamMember): boolean {
    const mine = this.auth.user()?.role;
    return this.canManage() && !this.isSelf(member) && !!mine && RANK[member.role] >= RANK[mine];
  }

  protected async changeRole(member: TeamMember, role: Role): Promise<void> {
    await this.run(async () => {
      const updated = await this.team.changeRole(member.id, role);
      this.members.update((list) => list.map((m) => (m.id === updated.id ? updated : m)));
      this.notify(`${updated.displayName} is now ${updated.role}.`);
    }, () => this.members.update((list) => [...list]));
  }

  protected async toggleActive(member: TeamMember): Promise<void> {
    await this.run(async () => {
      const updated = await this.team.setActive(member.id, !member.isActive);
      this.members.update((list) => list.map((m) => (m.id === updated.id ? updated : m)));
      this.notify(`${updated.displayName} was ${updated.isActive ? 'reactivated' : 'deactivated'}.`);
    });
  }

  protected async invite(): Promise<void> {
    if (this.inviteForm.invalid) {
      this.inviteForm.markAllAsTouched();
      return;
    }

    this.inviting.set(true);
    try {
      const { email, role } = this.inviteForm.getRawValue();
      const invitation = await this.team.invite(email, role);
      this.invitations.update((list) => [...list.filter((i) => i.email.toLowerCase() !== email.toLowerCase()), invitation]);
      this.inviteForm.reset({ email: '', role: 'Viewer' });
      this.notify(`Invitation sent to ${invitation.email}.`);
    } catch (error) {
      this.notify(toApiProblem(error).message);
    } finally {
      this.inviting.set(false);
    }
  }

  protected async revoke(invitation: Invitation): Promise<void> {
    await this.run(async () => {
      await this.team.revokeInvitation(invitation.id);
      this.invitations.update((list) => list.filter((i) => i.id !== invitation.id));
      this.notify(`Invitation for ${invitation.email} was revoked.`);
    });
  }

  protected rolePermissions(role: RoleInfo): Permission[] {
    return this.roleDrafts()[role.name] ?? role.permissions;
  }

  protected isRoleDirty(role: RoleInfo): boolean {
    return this.roleDrafts()[role.name] !== undefined;
  }

  protected togglePermission(role: RoleInfo, permission: Permission, granted: boolean): void {
    const current = this.rolePermissions(role);
    const next = granted ? [...current, permission] : current.filter((p) => p !== permission);
    this.roleDrafts.update((drafts) => ({ ...drafts, [role.name]: PERMISSIONS.filter((p) => next.includes(p)) }));
  }

  protected discardRole(role: RoleInfo): void {
    this.roleDrafts.update(({ [role.name]: _, ...rest }) => rest);
  }

  protected async saveRole(role: RoleInfo): Promise<void> {
    await this.run(async () => {
      const updated = await this.team.updateRolePermissions(role.name, this.rolePermissions(role));
      this.roles.update((list) => list.map((r) => (r.name === updated.name ? updated : r)));
      this.discardRole(role);
      await this.auth.reloadUser();
      this.notify(`Permissions for ${updated.name} were saved.`);
    });
  }

  private async reload(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [members, invitations, roles] = await Promise.all([this.team.members(), this.team.invitations(), this.team.roles()]);
      this.members.set(members);
      this.invitations.set(invitations);
      this.roles.set(roles);
    } catch (error) {
      this.loadError.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }

  private async run(action: () => Promise<void>, onError?: () => void): Promise<void> {
    try {
      await action();
    } catch (error) {
      onError?.();
      this.notify(toApiProblem(error).message);
    }
  }

  private notify(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 5000 });
  }
}
