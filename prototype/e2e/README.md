# Browser smoke test

The client-side half of the **automated smoke call** described in
[Deployment Strategy §7.5](../../docs/07-deployment-strategy.md) — the check that runs on every
deployment and catches the class of failure no unit test can: the app builds, the bundle loads,
the hub connects, and a call actually reaches an agent's screen.

It drives two real Chrome tabs through the Angular app and asserts 21 things end to end, including
the one that matters most: **a call placed in the supervisor's tab is offered in the agent's tab
over SignalR**, with a screen-pop and a working reservation token.

## Run it

```bash
# 1. the platform
dotnet run --project src/CallCenter.Api

# 2. a headless browser with the DevTools protocol exposed
chrome --headless=new --remote-debugging-port=9222 --user-data-dir=/tmp/cc-profile about:blank

# 3. the smoke test
node e2e/browser-smoke.mjs
```

Exits non-zero on the first failure and prints what the page actually said, so a CI log is enough
to diagnose it without reproducing locally.

## Why it is dependency-free

It speaks the Chrome DevTools Protocol over the `WebSocket` built into Node — about 60 lines. A
real project would likely use Playwright; at this size that would hide how little machinery an
end-to-end check needs, and it would add a browser download to a repo whose whole point is that
`dotnet run` is enough.
