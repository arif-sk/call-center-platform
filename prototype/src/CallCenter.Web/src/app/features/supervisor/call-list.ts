import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { CallView } from '../../core/models';

@Component({
  selector: 'app-call-list',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Calls</h2>
      <p class="hint">
        Select a call to reconstruct it from its events — the answer to "what happened to call X?"
        (NFR-M4).
      </p>

      <table>
        <thead>
          <tr>
            <th>Started</th><th>Dir</th><th>From → To</th><th>Queue</th><th>Pri</th>
            <th>Agent</th><th>Wait</th><th>Talk</th><th>RNA</th><th>Status</th><th>Disposition</th>
          </tr>
        </thead>
        <tbody>
          @for (call of calls(); track call.id) {
            <tr class="clickable" [class.selected]="call.id === selectedId()" (click)="selected.emit(call.id)">
              <td class="mono">{{ call.initiatedAt | date:'HH:mm:ss' }}</td>
              <td>{{ call.direction === 'Inbound' ? '←' : '→' }}</td>
              <td class="mono">
                {{ call.from }} → {{ call.to }}
                @if (call.contact) {
                  <div class="muted">{{ call.contact }}</div>
                }
              </td>
              <td>{{ call.queue ?? '—' }}</td>
              <td>{{ call.priority }}</td>
              <td>{{ call.agent ?? '—' }}</td>
              <td>{{ call.waitSeconds }}s</td>
              <td>{{ call.talkSeconds }}s</td>
              <td>{{ call.rnaCount || '' }}</td>
              <td><span class="pill" [class]="call.status">{{ call.status }}</span></td>
              <td class="muted">{{ call.disposition ?? '' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="11" class="muted">no calls yet</td></tr>
          }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    .clickable { cursor: pointer; }
    .selected { background: #eff6ff; }
  `,
})
export class CallListComponent {
  readonly calls = input<CallView[]>([]);
  readonly selectedId = input<string | null>(null);
  readonly selected = output<string>();
}
