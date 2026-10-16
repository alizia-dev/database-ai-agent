import { Routes } from '@angular/router';
import { authGuard, guestGuard, permissionGuard } from './core/auth/auth.guards';
import { PublicShell } from './layout/public-shell/public-shell';

export const routes: Routes = [
  {
    path: 'app',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/app-shell/app-shell').then((m) => m.AppShell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      {
        path: 'dashboard',
        title: 'Dashboard — Database AI Agent',
        loadComponent: () => import('./features/app/dashboard-page').then((m) => m.DashboardPage),
      },
      {
        path: 'database',
        title: 'Databases — Database AI Agent',
        canActivate: [permissionGuard('database.view')],
        loadComponent: () => import('./features/app/database-page').then((m) => m.DatabasePage),
      },
      {
        path: 'database/schema',
        title: 'Schema — Database AI Agent',
        canActivate: [permissionGuard('database.view')],
        loadComponent: () => import('./features/app/schema-page').then((m) => m.SchemaPage),
      },
      {
        path: 'database/connect',
        title: 'Connect a database — Database AI Agent',
        canActivate: [permissionGuard('database.manage')],
        loadComponent: () => import('./features/app/database-connect-page').then((m) => m.DatabaseConnectPage),
      },
      {
        path: 'tools',
        title: 'AI tools — Database AI Agent',
        canActivate: [permissionGuard('tools.view')],
        loadComponent: () => import('./features/app/tools-page').then((m) => m.ToolsPage),
      },
      {
        path: 'tools/:id',
        title: 'AI tool — Database AI Agent',
        canActivate: [permissionGuard('tools.view')],
        loadComponent: () => import('./features/app/tool-detail-page').then((m) => m.ToolDetailPage),
      },
      {
        path: 'team',
        title: 'Team — Database AI Agent',
        canActivate: [permissionGuard('users.view')],
        loadComponent: () => import('./features/app/team-page').then((m) => m.TeamPage),
      },
      {
        path: 'billing',
        title: 'Billing — Database AI Agent',
        canActivate: [permissionGuard('billing.view')],
        loadComponent: () => import('./features/app/billing-page').then((m) => m.BillingPage),
      },
      {
        path: 'settings',
        title: 'Settings — Database AI Agent',
        loadComponent: () => import('./features/app/settings-page').then((m) => m.SettingsPage),
      },
    ],
  },
  {
    path: '',
    component: PublicShell,
    children: [
      {
        path: '',
        pathMatch: 'full',
        title: 'Database AI Agent — Talk to your business database',
        loadComponent: () => import('./features/landing/landing-page').then((m) => m.LandingPage),
      },
      {
        path: 'pricing',
        title: 'Pricing — Database AI Agent',
        loadComponent: () => import('./features/pricing/pricing-page').then((m) => m.PricingPage),
      },
      {
        path: 'login',
        title: 'Sign in — Database AI Agent',
        canActivate: [guestGuard],
        loadComponent: () => import('./features/auth/login-page').then((m) => m.LoginPage),
      },
      {
        path: 'register',
        title: 'Create account — Database AI Agent',
        canActivate: [guestGuard],
        loadComponent: () => import('./features/auth/register-page').then((m) => m.RegisterPage),
      },
      {
        path: 'forgot-password',
        title: 'Reset password — Database AI Agent',
        loadComponent: () => import('./features/auth/forgot-password-page').then((m) => m.ForgotPasswordPage),
      },
      {
        path: 'reset-password',
        title: 'Choose a new password — Database AI Agent',
        loadComponent: () => import('./features/auth/reset-password-page').then((m) => m.ResetPasswordPage),
      },
      {
        path: 'verify-email',
        title: 'Verify email — Database AI Agent',
        loadComponent: () => import('./features/auth/verify-email-page').then((m) => m.VerifyEmailPage),
      },
      {
        path: 'accept-invite',
        title: 'Join your team — Database AI Agent',
        canActivate: [guestGuard],
        loadComponent: () => import('./features/auth/accept-invite-page').then((m) => m.AcceptInvitePage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
