import { Routes } from '@angular/router';
import { authenticatedGuard, supervisorGuard } from './core/guards';

/**
 * Lazy-loaded feature routes. At this size it changes little, but it establishes the pattern: a
 * supervisor never downloads the agent desktop, and an agent never downloads the reporting views.
 */
export const routes: Routes = [
  {
    path: 'login',
    title: 'Sign in — Call Center',
    loadComponent: () => import('./features/login/login').then(m => m.LoginComponent),
  },
  {
    path: 'agent',
    title: 'Agent Desktop',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./features/agent/agent-desktop').then(m => m.AgentDesktopComponent),
  },
  {
    path: 'supervisor',
    title: 'Supervisor Console',
    canActivate: [supervisorGuard],
    loadComponent: () => import('./features/supervisor/supervisor-console').then(m => m.SupervisorConsoleComponent),
  },
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  { path: '**', redirectTo: 'login' },
];
