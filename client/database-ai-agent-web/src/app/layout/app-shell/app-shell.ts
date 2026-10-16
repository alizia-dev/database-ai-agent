import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { Permission } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { WorkspaceService } from '../../core/workspace/workspace.service';

interface NavItem {
  label: string;
  icon: string;
  link: string;
  permission?: Permission;
}

/** Navigation grows as phases land; items appear only for users who hold the permission. */
const NAV_ITEMS: NavItem[] = [
  { label: 'Dashboard', icon: 'space_dashboard', link: '/app/dashboard' },
  { label: 'Databases', icon: 'storage', link: '/app/database', permission: 'database.view' },
  { label: 'Team', icon: 'group', link: '/app/team', permission: 'users.view' },
  { label: 'Billing', icon: 'credit_card', link: '/app/billing', permission: 'billing.view' },
  { label: 'Settings', icon: 'settings', link: '/app/settings' },
];

@Component({
  selector: 'app-app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatButtonModule, MatMenuModule],
  templateUrl: './app-shell.html',
  styleUrl: './app-shell.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShell implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly workspace = inject(WorkspaceService);

  protected readonly isHandset = toSignal(
    inject(BreakpointObserver).observe('(max-width: 899px)').pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly workspaceName = computed(() => this.workspace.settings()?.name ?? 'Workspace');
  protected readonly navItems = computed(() => {
    this.auth.user();
    return NAV_ITEMS.filter((item) => !item.permission || this.auth.hasPermission(item.permission));
  });

  ngOnInit(): void {
    this.workspace.load().catch(() => {
      // Navigation still works without the workspace name; pages show their own errors.
    });
  }

  protected logout(): Promise<void> {
    return this.auth.logout();
  }
}
