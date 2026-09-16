/**
 * Renders the Markdown design documents into three PDFs for submission.
 *
 * Mermaid diagrams are the reason this is not a one-liner: they only exist once a browser has
 * drawn them. So the pipeline is markdown -> HTML -> a real Chrome page (which runs Mermaid) ->
 * Page.printToPDF over the DevTools protocol, which is also what gives us proper running headers
 * and page numbers.
 *
 * Usage:  node build.mjs          (needs Chrome installed; finds it automatically)
 */

import { createServer } from 'node:http';
import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { marked } from 'marked';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '../..');
const SOURCE = path.join(REPO, 'docs', 'source');
const OUT = path.join(REPO, 'docs');

const AUTHOR = 'System Analyst / Solution Architect';
const PROJECT = 'In-House Call Center Platform';

/**
 * One document, sized for someone who has to read it before an interview rather than file it.
 *
 * The long-form working notes (the original seven documents) stay in docs/source/ — they are
 * where the thinking is kept and revised. This is the version that gets handed over.
 */
const GROUPS = [
  {
    slug: 'Call-Center-Platform-Design',
    title: 'Solution Design',
    subtitle:
      'What we would build, what to build first, how it grows, and how we would put it live safely',
    files: ['call-center-platform-design.md'],
  },
];

// ---------------------------------------------------------------- markdown

/**
 * Cross-document Markdown links are meaningless once three files become one PDF, so they collapse
 * to their link text. External links survive.
 */
function flattenLinks(md) {
  return md.replace(/\[([^\]]+)\]\((?!https?:)[^)]*\)/g, '$1');
}

/** The per-file "Previous / Next" navigation only made sense when these were separate files. */
function stripNavigation(md) {
  return md
    .split('\n')
    .filter(line => !/^\*\*(Previous|Next|Back to):/.test(line.trim()))
    .join('\n')
    .replace(/\n-{3,}\s*$/g, '')
    .trimEnd();
}

function renderer() {
  const r = new marked.Renderer();

  // Mermaid blocks must reach the page as source for the browser to draw, not as <pre><code>.
  r.code = ({ text, lang }) =>
    lang === 'mermaid'
      ? `<figure class="diagram"><div class="mermaid">${text}</div></figure>`
      : `<pre><code>${escapeHtml(text)}</code></pre>`;

  return r;
}

function escapeHtml(value) {
  return value.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

/** Builds a contents list from the H1 (document) and H2 (section) headings. */
function buildToc(markdown) {
  const entries = [];

  for (const line of markdown.split('\n')) {
    const h1 = /^# (.+)$/.exec(line);
    const h2 = /^## (.+)$/.exec(line);
    if (h1) entries.push({ level: 1, text: h1[1].trim() });
    else if (h2) entries.push({ level: 2, text: h2[1].trim() });
  }

  // With a single document the H1 just repeats the cover, so list the sections flat.
  if (entries.filter(e => e.level === 1).length <= 1) {
    const sections = entries
      .filter(e => e.level === 2)
      .map(e => `<li class="doc">${escapeHtml(e.text)}</li>`)
      .join('');

    return `<nav class="toc"><h2>Contents</h2><ol>${sections}</ol></nav>`;
  }

  let html = '<nav class="toc"><h2>Contents</h2><ol>';
  let open = false;

  for (const entry of entries) {
    if (entry.level === 1) {
      if (open) html += '</ul></li>';
      html += `<li class="doc">${escapeHtml(entry.text)}<ul>`;
      open = true;
    } else if (open) {
      html += `<li>${escapeHtml(entry.text)}</li>`;
    }
  }

  if (open) html += '</ul></li>';
  return html + '</ol></nav>';
}

// ---------------------------------------------------------------- page template

const STYLES = `
  @page { size: A4; margin: 16mm 14mm 15mm 14mm; }

  :root {
    --ink: #16202c; --muted: #5b6b7c; --line: #d9e0e8;
    --accent: #1d4ed8; --soft: #f6f8fa;
  }

  * { box-sizing: border-box; }

  body {
    margin: 0;
    font: 9.8pt/1.42 "Segoe UI", system-ui, -apple-system, sans-serif;
    color: var(--ink);
    -webkit-print-color-adjust: exact;
    print-color-adjust: exact;
  }

  /* ---- cover ---- */
  .cover { padding: 24mm 0 6mm; page-break-after: avoid; }
  .cover .project { font-size: 12pt; letter-spacing: .18em; text-transform: uppercase; color: var(--muted); }
  .cover h1 {
    font-size: 30pt; line-height: 1.15; margin: 14px 0 10px; font-weight: 700;
    letter-spacing: -.01em; border-bottom: 0; padding: 0; page-break-before: avoid;
  }
  .cover .subtitle { font-size: 13pt; color: var(--muted); max-width: 135mm; line-height: 1.45; }
  .cover .rule { width: 56px; height: 4px; background: var(--accent); margin: 18px 0; }
  .cover .meta { font-size: 10pt; color: var(--muted); }
  .cover .meta b { color: var(--ink); font-weight: 600; }
  .cover .part { margin-top: 34px; font-size: 9.5pt; color: var(--muted); }
  .cover .part span { display: inline-block; border: 1px solid var(--line); border-radius: 3px; padding: 3px 9px; margin-right: 6px; }
  .cover .part .on { background: var(--accent); border-color: var(--accent); color: #fff; }

  /* ---- contents ---- */
  .toc { page-break-after: always; margin-top: 10mm; }
  .toc h2 { font-size: 14pt; margin: 0 0 10px; }
  .toc ol { list-style: none; padding: 0; margin: 0; }
  .toc li.doc { font-weight: 600; margin: 5px 0; font-size: 10.5pt; }
  .toc ul { list-style: none; padding-left: 14px; margin: 4px 0 0; }
  .toc ul li { font-weight: 400; color: var(--muted); padding: 1.5px 0; font-size: 9.5pt; }

  /* ---- headings ---- */
  h1 {
    font-size: 19pt; margin: 0 0 4px; padding-bottom: 6px;
    border-bottom: 3px solid var(--accent); page-break-before: always; page-break-after: avoid;
  }
  h2 { font-size: 13pt; margin: 16px 0 6px; page-break-after: avoid; }
  h3 { font-size: 10.6pt; margin: 12px 0 4px; page-break-after: avoid; }
  h4 { font-size: 10.5pt; margin: 12px 0 4px; page-break-after: avoid; }

  p { margin: 5px 0; orphans: 3; widows: 3; }
  a { color: var(--accent); text-decoration: none; }

  /* ---- tables ---- */
  table { width: 100%; border-collapse: collapse; margin: 8px 0 10px; font-size: 8.1pt; page-break-inside: auto; }
  thead { display: table-header-group; }
  tr { page-break-inside: avoid; }
  th {
    text-align: left; background: var(--soft); border-bottom: 1.5px solid var(--line);
    padding: 4px 6px; font-weight: 700; font-size: 7.8pt;
  }
  td { padding: 3.5px 6px; border-bottom: 1px solid #edf1f5; vertical-align: top; }

  /* ---- code + quotes ---- */
  pre {
    background: var(--soft); border: 1px solid var(--line); border-radius: 4px;
    padding: 9px 11px; overflow: hidden; page-break-inside: avoid; margin: 10px 0;
  }
  pre code { font: 8.2pt/1.45 "Cascadia Mono", Consolas, monospace; white-space: pre-wrap; word-break: break-word; }
  :not(pre) > code {
    font: 9pt "Cascadia Mono", Consolas, monospace;
    background: var(--soft); border: 1px solid var(--line); border-radius: 3px; padding: 0 3px;
  }

  blockquote {
    margin: 9px 0; padding: 7px 12px; border-left: 3px solid var(--accent);
    background: var(--soft); page-break-inside: avoid;
  }
  blockquote p { margin: 4px 0; }

  ul, ol { margin: 5px 0; padding-left: 18px; }
  li { margin: 1.5px 0; }

  hr { border: 0; border-top: 1px solid var(--line); margin: 12px 0; }

  /* ---- diagrams ---- */
  figure.diagram { margin: 10px 0; text-align: center; page-break-inside: avoid; }
  /* Cap tall diagrams so one flowchart cannot claim a whole page. */
  figure.diagram svg { max-width: 100%; height: auto; max-height: 118mm; }
`;

function page(group, bodyHtml, tocHtml) {
  const index = GROUPS.indexOf(group) + 1;
  const multiple = GROUPS.length > 1;

  // Only worth showing where the reader is when there is more than one document.
  const parts = multiple
    ? `<div class="part">${GROUPS
        .map((g, i) => `<span class="${g === group ? 'on' : ''}">${i + 1}. ${escapeHtml(g.title)}</span>`)
        .join('')}</div>`
    : '';

  const position = multiple ? `Document ${index} of ${GROUPS.length} &nbsp;·&nbsp; ` : '';

  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>${escapeHtml(group.title)}</title>
<style>${STYLES}</style></head>
<body>
  <section class="cover">
    <div class="project">${escapeHtml(PROJECT)}</div>
    <h1>${escapeHtml(group.title)}</h1>
    <div class="rule"></div>
    <div class="subtitle">${escapeHtml(group.subtitle)}</div>
    <div class="rule" style="background:var(--line);height:1px;width:100%"></div>
    <div class="meta">
      <b>${escapeHtml(AUTHOR)}</b><br>
      ${position}${new Date().toISOString().slice(0, 10)}
    </div>
    ${parts}
  </section>

  ${tocHtml}
  ${bodyHtml}

  <script src="mermaid.min.js"></script>
  <script>
    (async () => {
      const ns = window.mermaid || window.__esbuild_esm_mermaid_nm?.mermaid;
      const mermaid = ns?.default ?? ns;

      if (!mermaid) { window.__ready = 'no-mermaid'; return; }

      mermaid.initialize({
        startOnLoad: false,
        theme: 'neutral',
        securityLevel: 'loose',
        flowchart: { htmlLabels: true, useMaxWidth: true },
        sequence: { useMaxWidth: true },
        er: { useMaxWidth: true },
        themeVariables: { fontFamily: 'Segoe UI, system-ui, sans-serif', fontSize: '13px' },
      });

      const nodes = [...document.querySelectorAll('.mermaid')];
      window.__failures = [];

      // Render one at a time so a single bad diagram cannot abort the rest of the document.
      for (const node of nodes) {
        const source = node.textContent.trim();
        try {
          await mermaid.run({ nodes: [node] });
        } catch (err) {
          window.__failures.push({
            firstLine: source.slice(0, 64),
            message: String(err.message ?? err).slice(0, 200),
          });
          node.innerHTML = '<pre style="text-align:left">' + source.slice(0, 400) + '</pre>';
        }
      }

      window.__ready = 'ok:' + nodes.length + ':' + window.__failures.length;
    })();
  </script>
</body></html>`;
}

// ---------------------------------------------------------------- CDP

function findChrome() {
  const candidates = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    '/usr/bin/google-chrome',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ];

  const found = candidates.find(c => existsSync(c));
  if (!found) throw new Error('Chrome or Edge is required to render the diagrams. Install one, or set CHROME_PATH.');
  return process.env.CHROME_PATH ?? found;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

class Cdp {
  constructor(wsUrl) { this.wsUrl = wsUrl; this.id = 0; this.pending = new Map(); }

  async connect() {
    this.ws = new WebSocket(this.wsUrl);
    await new Promise((res, rej) => { this.ws.onopen = res; this.ws.onerror = rej; });
    this.ws.onmessage = e => {
      const msg = JSON.parse(e.data);
      const resolve = this.pending.get(msg.id);
      if (resolve) { this.pending.delete(msg.id); resolve(msg); }
    };
    return this;
  }

  send(method, params = {}) {
    const id = ++this.id;
    return new Promise(resolve => { this.pending.set(id, resolve); this.ws.send(JSON.stringify({ id, method, params })); });
  }

  async eval(expression) {
    const res = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    return res.result?.result?.value;
  }

  close() { this.ws.close(); }
}

// ---------------------------------------------------------------- build

async function main() {
  const work = path.join(tmpdir(), `ccdocs-${Date.now()}`);
  await mkdir(work, { recursive: true });
  await mkdir(OUT, { recursive: true });

  // Serve Mermaid alongside the pages: loading a 5 MB module over file:// is blocked by CORS.
  const mermaidJs = await readFile(path.join(HERE, 'node_modules/mermaid/dist/mermaid.min.js'));
  await writeFile(path.join(work, 'mermaid.min.js'), mermaidJs);

  marked.setOptions({ renderer: renderer(), gfm: true, breaks: false });

  for (const group of GROUPS) {
    const chunks = [];

    for (const file of group.files) {
      const raw = await readFile(path.join(SOURCE, file), 'utf8');
      // Normalise line endings first. Some sources are CRLF, and in a JavaScript regex "."
      // does not match a carriage return (it counts as a line terminator), so a heading
      // pattern like ^## (.+)$ silently fails on every CRLF line — which is how an entire
      // document went missing from the table of contents.
      const normalised = raw.replace(/\r\n?/g, '\n');
      chunks.push(stripNavigation(flattenLinks(normalised)));
    }

    const markdown = chunks.join('\n\n');
    const html = page(group, marked.parse(markdown), buildToc(markdown));
    await writeFile(path.join(work, `${group.slug}.html`), html, 'utf8');
  }

  // Static server for the generated pages.
  const server = createServer(async (req, res) => {
    const name = decodeURIComponent(req.url.split('?')[0]).replace(/^\//, '') || 'index.html';
    try {
      const body = await readFile(path.join(work, name));
      res.writeHead(200, { 'Content-Type': name.endsWith('.js') ? 'text/javascript' : 'text/html' });
      res.end(body);
    } catch {
      res.writeHead(404).end('not found');
    }
  });

  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;

  const profile = path.join(tmpdir(), `ccprofile-${Date.now()}`);
  const chrome = spawn(findChrome(), [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--remote-debugging-port=9333', `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: 'ignore' });

  // Wait for the DevTools endpoint rather than guessing at a sleep duration.
  let version = null;
  for (let i = 0; i < 40 && !version; i++) {
    try { version = await (await fetch('http://127.0.0.1:9333/json/version')).json(); }
    catch { await sleep(250); }
  }
  if (!version) throw new Error('Chrome did not expose its DevTools endpoint.');

  const built = [];

  try {
    for (const group of GROUPS) {
      const target = await (await fetch(
        `http://127.0.0.1:9333/json/new?${encodeURIComponent(`http://127.0.0.1:${port}/${group.slug}.html`)}`,
        { method: 'PUT' },
      )).json();

      const tab = await new Cdp(target.webSocketDebuggerUrl).connect();
      await tab.send('Page.enable');
      await tab.send('Runtime.enable');

      // Wait for every diagram to finish drawing before printing.
      let status = null;
      for (let i = 0; i < 120 && !status; i++) {
        status = await tab.eval('window.__ready');
        if (!status) await sleep(500);
      }
      if (!status) throw new Error(`${group.slug}: diagrams never finished rendering`);

      const [, count = '0', failed = '0'] = String(status).split(':');

      // A diagram that fails to parse would ship as a blank space in a document someone is
      // being asked to make a decision from. Fail the build instead.
      // (Mermaid treats ";" as a statement separator, so a semicolon inside a label silently
      // truncates the diagram — which is exactly how two of these shipped broken.)
      if (Number(failed) > 0) {
        const failures = JSON.parse((await tab.eval('JSON.stringify(window.__failures)')) ?? '[]');
        console.error(`
  ${failed} diagram(s) failed to render in "${group.title}":`);
        for (const f of failures) console.error(`    ${f.firstLine.replace(/\s+/g, ' ')}
      -> ${f.message}`);
        throw new Error('Diagram rendering failed — fix the source before publishing.');
      }

      const pdf = await tab.send('Page.printToPDF', {
        printBackground: true,
        preferCSSPageSize: true,
        displayHeaderFooter: true,
        headerTemplate: `<div style="font-size:7pt;color:#9aa7b4;width:100%;padding:0 16mm;
            font-family:Segoe UI,sans-serif;display:flex;justify-content:space-between;">
            <span>${escapeHtml(PROJECT)}</span><span>${escapeHtml(group.title)}</span></div>`,
        footerTemplate: `<div style="font-size:7pt;color:#9aa7b4;width:100%;padding:0 16mm;
            font-family:Segoe UI,sans-serif;display:flex;justify-content:space-between;">
            <span>${escapeHtml(AUTHOR)}</span>
            <span><span class="pageNumber"></span> / <span class="totalPages"></span></span></div>`,
      });

      const file = path.join(OUT, `${group.slug}.pdf`);
      await writeFile(file, Buffer.from(pdf.result.data, 'base64'));
      built.push({ file, diagrams: Number(count) });

      console.log(`  ${path.basename(file)}  (${count} diagrams)`);
      tab.close();
    }
  } finally {
    chrome.kill();
    server.close();
  }

  console.log(`\n  ${built.length} PDFs written to docs/`);
}

main().catch(err => { console.error('\n  ERROR:', err.message); process.exit(1); });
