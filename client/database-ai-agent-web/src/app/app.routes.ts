import { Routes } from '@angular/router';
import { PublicShell } from './layout/public-shell/public-shell';

export const routes: Routes = [
  {
    path: '',
    component: PublicShell,
    children: [
      {
        path: '',
        title: 'Database AI Agent — Talk to your business database',
        loadComponent: () => import('./features/landing/landing-page').then((m) => m.LandingPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
