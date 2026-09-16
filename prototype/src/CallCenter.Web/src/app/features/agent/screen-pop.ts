import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { ScreenPopMessage } from '../../core/models';

/**
 * Customer context, resolved from the caller's number while the phone is already ringing.
 *
 * All four outcomes are rendered deliberately, because three of them are the interesting ones:
 * an unknown number needs a create-contact path (FR-F2), an ambiguous match must never be guessed
 * (FR-F4), and an unavailable CRM must look obviously degraded rather than simply empty — the agent
 * needs to know the blank panel is the CRM's fault, not the customer's (FR-F5).
 */
@Component({
  selector: 'app-screen-pop',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Customer (screen-pop)</h2>
      <p class="hint">
        Resolved from the caller's number in parallel with the phone ringing — a slow CRM can never
        delay a call (FR-F1, FR-F5).
      </p>

      @if (pop(); as result) {
        @switch (result.outcome) {
          @case ('Found') {
            @if (result.contact; as contact) {
              <div class="name">{{ contact.displayName }}</div>
              <div class="muted">{{ contact.company }} · tier {{ contact.tier }}</div>
              <div class="spacer"></div>
              <dl class="dl">
                <dt>CRM id</dt><dd class="mono">{{ contact.externalId }}</dd>
                <dt>Last contact</dt><dd>{{ contact.lastInteraction }}</dd>
              </dl>
            }
          }

          @case ('Unknown') {
            <b>Unknown number.</b>
            <div class="muted">Offer to create a new contact, pre-filled with the number (FR-F2).</div>
          }

          @case ('Ambiguous') {
            <b>{{ result.candidates.length }} contacts share this number.</b>
            <div class="muted">Ask the caller which one — we never guess (FR-F4).</div>
            <ul>
              @for (candidate of result.candidates; track candidate.externalId) {
                <li>{{ candidate.displayName }} — {{ candidate.company }}</li>
              }
            </ul>
          }

          @case ('Unavailable') {
            <b class="bad">CRM unavailable.</b>
            <div class="muted">Screen-pop degraded; the call itself is unaffected (FR-F5).</div>
          }
        }

        <div class="spacer"></div>
        <div class="muted mono">lookup {{ result.elapsedMs }} ms</div>
      } @else {
        <span class="muted">No active call.</span>
      }
    </section>
  `,
  styles: `
    .name { font-size: 17px; font-weight: 700; }
    .bad { color: var(--bad); }
    ul { margin: 8px 0 0; padding-left: 18px; }
  `,
})
export class ScreenPopComponent {
  readonly pop = input<ScreenPopMessage | null>(null);
}
