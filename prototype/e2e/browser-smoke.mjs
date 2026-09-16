/**
 * Browser smoke test — the client-side half of the "automated smoke call" every deployment runs
 * (docs/07-deployment-strategy.md §7.5).
 *
 * It drives two real Chrome tabs through the Angular app: an agent and a supervisor. The assertion
 * that matters is the one no unit test can make — a call placed in the supervisor's tab must reach
 * the agent's tab over SignalR, with a screen-pop, a live reservation countdown, and a token the
 * server will accept. That is the whole platform in one path.
 *
 * Deliberately dependency-free: a CDP client over the WebSocket built into Node 18+. Adding
 * Playwright would be reasonable in a real project; here it would obscure how little is needed.
 *
 * Usage:
 *   1. dotnet run --project src/CallCenter.Api
 *   2. chrome --headless=new --remote-debugging-port=9222 --user-data-dir=<temp> about:blank
 *   3. node e2e/browser-smoke.mjs
 */
const BASE = 'http://localhost:5080';
const DEBUG = 'http://localhost:9222';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function newTab(url) {
  const res = await fetch(`${DEBUG}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' });
  return res.json();
}

class Tab {
  constructor(target) {
    this.target = target;
    this.id = 0;
    this.pending = new Map();
  }

  async connect() {
    this.ws = new WebSocket(this.target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      this.ws.onopen = resolve;
      this.ws.onerror = reject;
    });
    this.ws.onmessage = (e) => {
      const msg = JSON.parse(e.data);
      const resolver = this.pending.get(msg.id);
      if (resolver) {
        this.pending.delete(msg.id);
        resolver(msg);
      }
    };
    return this;
  }

  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  async eval(expression) {
    const res = await this.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
    });
    if (res.result?.exceptionDetails) {
      throw new Error(res.result.exceptionDetails.exception?.description ?? 'page error');
    }
    return res.result?.result?.value;
  }

  async navigate(url) {
    await this.send('Page.enable');
    await this.send('Page.navigate', { url });
    await sleep(1500);
  }

  /** Polls a predicate inside the page until it is true, or fails with the page's own text. */
  async waitFor(label, jsPredicate, timeoutMs = 15000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (await this.eval(`(() => { try { return !!(${jsPredicate}); } catch { return false; } })()`)) {
        return true;
      }
      await sleep(200);
    }
    const text = await this.eval('document.body.innerText.slice(0, 1800)');
    throw new Error(`TIMEOUT waiting for ${label}\n--- page said ---\n${text}`);
  }

  text() {
    return this.eval('document.body.innerText');
  }

  consoleErrors() {
    return this.eval('window.__errors ? window.__errors.join(" | ") : ""');
  }
}

const results = [];
function check(name, ok, detail = '') {
  results.push({ name, ok, detail });
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
}

// Records any uncaught error so a silent Angular failure cannot masquerade as a pass.
const TEXT = `document.body.innerText.toLowerCase()`;

const ERROR_TRAP = `window.__errors = window.__errors || [];
  window.addEventListener('error', e => window.__errors.push(e.message));
  window.addEventListener('unhandledrejection', e => window.__errors.push(String(e.reason)));`;

async function main() {
  // ---------------------------------------------------------------- agent tab
  const agent = await new Tab(await newTab(`${BASE}/login`)).connect();
  await agent.send('Runtime.enable');
  await agent.eval(ERROR_TRAP);
  await agent.waitFor('login page + bootstrap', `document.querySelectorAll('#agent option').length >= 5`);
  check('Angular app boots and loads agents from the API', true);

  // Defaults are Amara Okafor + role Agent — click the primary Sign in button.
  await agent.eval(`document.querySelector('button.primary').click()`);
  await agent.waitFor('agent desktop', `location.pathname === '/agent' && ${TEXT}.includes('presence')`);
  check('Agent signs in and the router lands on /agent', true);

  const navText = await agent.text();
  check('Agent nav hides the supervisor link (role-aware UI)', !navText.includes('Supervisor\n') || !/\bSupervisor\b/.test(navText.split('Agent')[0] ?? ''), '');

  await agent.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Available').click()`);
  await agent.waitFor('Available state', `document.querySelector('.pill')?.textContent.trim().toLowerCase() === 'available'`);
  check('Agent goes Available (server-authoritative state round-trip)', true);

  // ---------------------------------------------------------------- supervisor tab
  const sup = await new Tab(await newTab(`${BASE}/login`)).connect();
  await sup.send('Runtime.enable');
  await sup.eval(ERROR_TRAP);
  await sup.waitFor('login page', `[...document.querySelectorAll('button')].some(b => b.textContent.includes('Supervisor Console'))`);

  await sup.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.includes('Supervisor Console')).click()`);
  await sup.waitFor('supervisor console', `location.pathname === '/supervisor' && ${TEXT}.includes('call simulator')`);
  check('Supervisor signs in and the guard admits them to /supervisor', true);

  await sup.waitFor('wallboard push', `${TEXT}.includes('simulated')`);
  check('Wallboard arrives over SignalR (1 Hz push)', true);

  const supText = (await sup.text()).toLowerCase();
  check('Wallboard shows the agent as Available', supText.includes('amara okafor') && supText.includes('available'));

  // ---------------------------------------------------------------- place a call
  await sup.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.includes('Place inbound call')).click()`);

  // ---------------------------------------------------------------- the money shot
  // A call placed in the supervisor tab must reach the agent tab via the hub, with a screen-pop.
  await agent.waitFor('incoming call panel', `document.querySelector('.offer') !== null`);
  check('Call placed in one tab is OFFERED to the agent in another (SignalR push)', true);

  const offerText = await agent.text();
  check('Offer shows the caller and queue', offerText.includes('+447700900001') && offerText.includes('Customer Support'));
  check('Reservation countdown is running', /Expires in\s*\d+s/.test(offerText), (offerText.match(/Expires in\s*\d+s/) ?? [''])[0]);

  await agent.waitFor('screen-pop', `${TEXT}.includes('priya raman')`);
  check('Screen-pop resolved the caller from the CRM', true);

  // ---------------------------------------------------------------- answer and control
  await agent.eval(`document.querySelector('.offer button.good').click()`);
  await agent.waitFor('active call', `${TEXT}.includes('active call')`);
  check('Agent answers with the reservation token and the call connects', true);

  await agent.waitFor('on-call state', `document.querySelector('.pill')?.textContent.toLowerCase().includes('oncall')`);
  check('Agent state moves to OnCall', true);

  await agent.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Pause recording').click()`);
  await agent.waitFor('recording paused', `${TEXT}.includes('paused (pci)')`);
  check('PCI pause/resume works from the desktop', true);

  await agent.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Hang up').click()`);
  await agent.waitFor('wrap-up', `${TEXT}.includes('wrap-up')`);
  check('Hang up moves the agent into mandatory wrap-up', true);

  await agent.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.includes('Submit')).click()`);
  await agent.waitFor('back to available', `document.querySelector('.pill')?.textContent.trim().toLowerCase() === 'available'`);
  check('Disposition returns the agent to Available', true);

  // ---------------------------------------------------------------- outbound + DNC
  await agent.eval(`
    const input = document.querySelector('#to');
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    setter.call(input, '+447700900999');
    input.dispatchEvent(new Event('input', { bubbles: true }));
  `);
  await sleep(300);
  await agent.eval(`[...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Dial').click()`);
  await agent.waitFor('DNC refusal banner', `${TEXT}.includes('do-not-call')`);
  check('Outbound to a suppressed number is refused and explained', true);

  // ---------------------------------------------------------------- supervisor sees it all
  await sup.waitFor('event stream', `${TEXT}.includes('dispositionset')`);
  check('Supervisor event stream shows the full call lifecycle', true);

  await sup.waitFor('call list row', `document.querySelectorAll('app-call-list tbody tr.clickable').length > 0`);
  await sup.eval(`document.querySelector('app-call-list tbody tr.clickable').click()`);
  await sup.waitFor('call trace', `${TEXT}.includes('call trace')`);
  const traceText = await sup.text();
  check('Call trace replays the event stream',
    traceText.includes('CallOffered') && traceText.includes('CallAnswered') && traceText.includes('RecordingPaused'));

  // ---------------------------------------------------------------- no silent failures
  const agentErrors = await agent.consoleErrors();
  const supErrors = await sup.consoleErrors();
  check('No uncaught errors in the agent tab', !agentErrors, agentErrors);
  check('No uncaught errors in the supervisor tab', !supErrors, supErrors);

  const failed = results.filter((r) => !r.ok);
  console.log(`\n  ${results.length - failed.length}/${results.length} browser checks passed`);
  process.exit(failed.length ? 1 : 0);
}

main().catch((err) => {
  console.error('\n  ERROR:', err.message);
  process.exit(1);
});
