import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { CallCenterService } from './call-center.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  template: `
    <header class="topbar">
      <a routerLink="/" class="brand">Call Center</a>
      <nav>
        <a routerLink="/agent">Agent desktop</a>
        <a routerLink="/supervisor">Supervisor</a>
      </nav>
      <span class="link-state" [class.on]="cc.connected()">
        {{ cc.connected() ? 'Live' : 'Reconnecting…' }}
      </span>
    </header>

    @if (cc.error(); as message) {
      <div class="banner" role="alert">
        {{ message }}
        <button type="button" (click)="cc.dismissError()">Dismiss</button>
      </div>
    }

    <main>
      <router-outlet />
    </main>
  `,
})
export class App {
  protected readonly cc = inject(CallCenterService);
}
