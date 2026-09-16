import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: 'login',
    title: 'Sign in — Call Center',
    loadComponent: () => import('./login').then((m) => m.LoginComponent),
  },
  {
    path: 'agent',
    title: 'Agent Desktop',
    loadComponent: () => import('./agent-desktop').then((m) => m.AgentDesktopComponent),
  },
  {
    path: 'supervisor',
    title: 'Supervisor Console',
    loadComponent: () => import('./supervisor-console').then((m) => m.SupervisorConsoleComponent),
  },
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  { path: '**', redirectTo: 'login' },
];
