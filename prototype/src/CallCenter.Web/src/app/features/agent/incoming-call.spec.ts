import { ComponentFixture, TestBed } from '@angular/core/testing';
import { IncomingCallComponent } from './incoming-call';
import { CallOfferedMessage } from '../../core/models';

function offer(secondsFromNow: number): CallOfferedMessage {
  return {
    callId: 'c1',
    reservationToken: 't1',
    expiresAt: new Date(Date.now() + secondsFromNow * 1000).toISOString(),
    direction: 'Inbound',
    from: '+447700900001',
    to: '+442045550100',
    queue: 'Customer Support',
    waitSeconds: 12,
    ringTimeoutSeconds: 15,
  };
}

/**
 * The countdown is the agent's only warning before the reservation expires and the platform
 * treats it as ring-no-answer (FR-C7). Worth pinning: it must never read negative, and it must
 * disappear entirely when there is no offer.
 */
describe('IncomingCallComponent', () => {
  let fixture: ComponentFixture<IncomingCallComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [IncomingCallComponent] }).compileComponents();
    fixture = TestBed.createComponent(IncomingCallComponent);
  });

  it('renders nothing when no call is offered', () => {
    fixture.componentRef.setInput('offer', null);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.offer')).toBeNull();
  });

  it('shows the caller, the queue and a countdown', () => {
    fixture.componentRef.setInput('offer', offer(15));
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('+447700900001');
    expect(text).toContain('Customer Support');
    expect(fixture.componentInstance.secondsLeft()).toBeGreaterThan(10);
  });

  it('never counts below zero once the reservation has expired', () => {
    fixture.componentRef.setInput('offer', offer(-30));
    fixture.detectChanges();

    expect(fixture.componentInstance.secondsLeft()).toBe(0);
  });

  it('emits answer and reject rather than calling the API itself', () => {
    fixture.componentRef.setInput('offer', offer(15));
    fixture.detectChanges();

    const answered: unknown[] = [];
    const rejected: unknown[] = [];
    fixture.componentInstance.answer.subscribe(() => answered.push(1));
    fixture.componentInstance.reject.subscribe(() => rejected.push(1));

    const buttons = fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>;
    buttons[0].click();
    buttons[1].click();

    expect(answered.length).toBe(1);
    expect(rejected.length).toBe(1);
  });
});
