import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

export interface Disposition { code: string; notes: string | null; }

/**
 * Wrap-up. A disposition is mandatory before the agent returns to Available (Q21) — it is the
 * label set every report and every future classification model depends on, so an optional field
 * here would quietly degrade both.
 */
@Component({
  selector: 'app-wrap-up-form',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Wrap-up</h2>
      <p class="hint">A disposition is mandatory before returning to Available (Q21).</p>

      <div class="stack">
        <div>
          <label for="disposition">Outcome</label>
          <select id="disposition" [(ngModel)]="code">
            @for (option of options(); track option) {
              <option [value]="option">{{ option }}</option>
            }
          </select>
        </div>

        <div>
          <label for="notes">Notes</label>
          <textarea id="notes" rows="2" [(ngModel)]="notes"></textarea>
        </div>

        <div class="row">
          <button class="primary" type="button" [disabled]="!code()" (click)="submit()">
            Submit &amp; go available
          </button>
        </div>
      </div>
    </section>
  `,
})
export class WrapUpFormComponent {
  readonly options = input<string[]>([]);
  readonly submitted = output<Disposition>();

  protected readonly code = signal('');
  protected readonly notes = signal('');

  constructor() {
    // Default to the first configured outcome so the common case is one click.
    queueMicrotask(() => {
      if (!this.code() && this.options().length) this.code.set(this.options()[0]);
    });
  }

  protected submit(): void {
    this.submitted.emit({ code: this.code(), notes: this.notes() || null });
    this.notes.set('');
  }
}
