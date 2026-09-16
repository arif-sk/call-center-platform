import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-outbound-dialer',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Outbound</h2>
      <p class="hint">
        Every attempt passes a synchronous do-not-call gate that fails closed (FR-D4).
        <b>+44 7700 900999</b> is on the suppression list — try it.
      </p>

      <div class="row">
        <div style="flex:2">
          <label for="to">Number</label>
          <input id="to" [(ngModel)]="number" autocomplete="off">
        </div>
        <button class="primary" type="button" [disabled]="disabled() || !number()" (click)="dial.emit(number())">
          Dial
        </button>
      </div>
    </section>
  `,
})
export class OutboundDialerComponent {
  readonly disabled = input(false);
  readonly dial = output<string>();

  protected readonly number = signal('07700 900002');
}
