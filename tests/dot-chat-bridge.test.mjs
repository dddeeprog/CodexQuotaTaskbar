import {readFileSync} from 'node:fs';
import {runInNewContext} from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

// Works both alongside the task candidate and after copying into the repository's tests directory.
let source;
try { source = readFileSync(new URL('./DotChatBridge.js', import.meta.url), 'utf8'); }
catch { source = readFileSync(new URL('../src/CodexQuotaTaskbar.Host/Provider/DotChatBridge.js', import.meta.url), 'utf8'); }
const payload = 'Offline fixture payload';
class Element {
    constructor(tag, {text = '', classes = [], attrs = {}, children = []} = {}) {
        this.tag = tag; this.tagName = tag.toUpperCase(); this.nodeType = 1; this.ownText = text; this.classes = classes; this.attrs = attrs;
        this.children = children; for (const child of children) child.parent = this;
        this.classList = {contains: name => this.classes.includes(name)};
    }
    get innerText() { return this.forceInnerTextEmpty ? '' : this.ownText + this.children.map(child => child.innerText).join(''); }
    set innerText(value) { this.ownText = value; this.children = []; }
    get textContent() { return this.ownText + this.children.map(child => child.textContent).join(''); }
    get childNodes() { return [...(this.ownText ? [{nodeType:3, nodeValue:this.ownText, parentElement:this}] : []), ...this.children]; }
    get parentElement() { return this.parent || null; }
    contains(node) { return node === this || this.children.some(child => child.contains(node)); }
    getAttribute(name) { return this.attrs[name] ?? null; }
    getClientRects() { return [1]; }
    focus() {}
    matches(selector) {
        selector = selector.trim();
        if (selector.startsWith('.')) return this.classList.contains(selector.slice(1));
        const match = /^(\w+)?(?:\[([^=\]]+)(?:="([^"]*)")?\])?$/.exec(selector);
        return !!match && (!match[1] || this.tag === match[1]) && (!match[2] ||
            (match[3] === undefined ? this.getAttribute(match[2]) !== null : this.getAttribute(match[2]) === match[3]));
    }
    querySelectorAll(selector) {
        const selectors = selector.split(','); const found = [];
        const visit = node => { for (const child of node.children) {
            if (selectors.some(part => child.matches(part))) found.push(child);
            visit(child);
        }};
        visit(this); return found;
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    cloneNode() { return new Element(this.tag, {text:this.ownText, classes:[...this.classes], attrs:{...this.attrs}, children:this.children.map(child => child.cloneNode(true))}); }
    remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
}
function article(text, {id = 'message-0', role = 'assistant', nestedId = false, attributeRole = null,
    roleAttributeOnBody = false, body = 'message-content', controlsInside = false, conflictId = null} = {}) {
    const content = new Element('div', {text, classes:body ? [body] : [],
        attrs:roleAttributeOnBody ? {'data-message-author-role':attributeRole || role} : {},
        children:controlsInside ? [new Element('button', {text:'COPY-CONTROL'})] : []});
    const bubble = new Element('div', {classes:['message-bubble-wrap'], children:[content]});
    const bodyChildren = [bubble, new Element('button', {text:'OUTSIDE-ACTION'}),
        new Element('time', {text:' 14:30', attrs:{datetime:'2026-10-23T06:30:00Z'}})];
    if (nestedId && id !== null) bubble.attrs['data-message-id'] = id;
    if (conflictId) content.attrs['data-message-id'] = conflictId;
    return new Element('article', {classes:role ? ['message-row', ...(role === 'user' ? ['self'] : [])] : [],
        attrs:{...(!nestedId && id !== null ? {'data-message-id':id} : {}),
            ...(attributeRole && !roleAttributeOnBody ? {'data-message-author-role':attributeRole} : {})}, children:bodyChildren});
}
function liveArticle(text, {actionText = '', actionContainer = 'class', bodyChildren = null, secondBody = false,
    outerDecoration = '', role = 'user', id = 'live-message'} = {}) {
    const icon = new Element('button', {text:actionText, classes:['ghost','icon-button','message-action-button'], attrs:{'aria-label':'Copy'}});
    const actions = actionContainer === 'none' ? icon : new Element('div', {
        classes:actionContainer === 'class' ? ['message-inline-actions','message-inline-actions--orbit'] : [],
        attrs:actionContainer === 'attribute' ? {'data-message-actions':''} : {}, children:[icon]});
    const body = new Element('div', {text:bodyChildren ? '' : text, classes:['message-body'], children:[...(bodyChildren || []), actions]});
    const content = new Element('div', {text:outerDecoration, classes:['message-content'], children:[body,
        ...(secondBody ? [new Element('div', {text:'Additional real body', classes:['message-body']})] : [])]});
    const stack = new Element('div', {classes:['message-content-stack'], children:[content]});
    const bubble = new Element('div', {classes:['message-bubble-wrap'], children:[stack]});
    return new Element('article', {attrs:{'data-message-id':id}, classes:['message-row', ...(role === 'user' ? ['self'] : [])], children:[bubble]});
}
function fixture({origin = 'https://chatgpt.com', path = '/dots/home', draft = '', count = 1,
    articles = [], busy = false, insertFails = false, onInsert = null, clickThrows = false} = {}) {
    let clicks = 0, insertions = 0;
    const editor = new Element('div', {text:draft});
    const send = new Element('button', {attrs:{'aria-label':'Send message'}});
    send.disabled = false;
    send.click = () => { clicks++; editor.innerText = ''; if (clickThrows) throw new Error('result lost after click'); };
    const stop = new Element('button', {attrs:{'aria-label':'Stop generating'}});
    const nodes = [...articles];
    const bridge = runInNewContext(source, {location:{origin, pathname:path}, document:{
        querySelectorAll: selector => selector === 'button' ? (busy ? [send, stop] : [send]) :
            selector === 'article' ? nodes : Array(count).fill(editor),
        execCommand: (_, __, text) => { insertions++; if (!insertFails) editor.innerText = text; onInsert?.(nodes); return !insertFails; }
    }, getComputedStyle: node => ({display:node.css?.display ||
        (['span','a','b','strong','em','i','code','small','br','wbr'].includes(node.tag) ? 'inline' : 'block'),
        visibility:node.css?.visibility || 'visible', contentVisibility:node.css?.contentVisibility || 'visible'})});
    return {call:(action, text = payload, probe = {}) => bridge(action, text, probe), editor, send, nodes,
        stats:() => ({clicks, insertions}), prepare(text = payload) { return bridge('prepare', text); },
        submit(prepared, text = payload) { return bridge('submit', text,
            {beforeIds:prepared.beforeIds, baselineComplete:prepared.baselineComplete}); }};
}
const safeArticle = (text = 'Prior visible message', id = 'old') => article(text, {id});
const plain = value => JSON.parse(JSON.stringify(value));
const normalizeFixtureText = value => value.trim().replace(/\s+/gu, ' ');

test('notification reads skip historical bodies but preserve the exact latest message window', () => {
    const nodes = Array.from({length:256}, (_, i) => article('synthetic '.repeat(300), {id:'history-' + i}));
    let bodyReads = 0;
    for (const node of nodes) {
        const body = node.querySelector('.message-content');
        Object.defineProperty(body, 'innerText', {get() { bodyReads++; return this.ownText; }});
    }
    const f = fixture({articles:nodes});
    const full = plain(f.call('read')); const fullReads = bodyReads;
    bodyReads = 0;
    const lean = plain(f.call('read', '', {notificationOnly:true}));
    assert.deepEqual(lean.messages, full.messages);
    assert.equal(lean.state, full.state); assert.equal(lean.draftEmpty, full.draftEmpty);
    assert.equal(lean.diagnostics.baselineComplete, full.diagnostics.baselineComplete);
    assert.equal(fullReads, 256); assert.equal(bodyReads, 12);
    assert.equal(lean.diagnostics.historicalBodiesSkipped, 244);
    assert.deepEqual(f.stats(), {clicks:0, insertions:0});
});

test('notification reads retain off-window duplicate, conflicting and missing identity checks', () => {
    for (const historical of [article('old', {id:'recent-0'}), article('old', {id:null}),
        article('old', {id:'old', conflictId:'conflict'})]) {
        const nodes = [historical, ...Array.from({length:12}, (_, i) => article('new', {id:'recent-' + i}))];
        const f = fixture({articles:nodes});
        const full = plain(f.call('read')); const lean = plain(f.call('read', '', {notificationOnly:true}));
        assert.deepEqual(lean.messages, full.messages);
        assert.equal(lean.diagnostics.baselineComplete, false);
        assert.equal(lean.diagnostics.missingIds, full.diagnostics.missingIds);
        assert.equal(lean.diagnostics.duplicateIds, full.diagnostics.duplicateIds);
    }
});

test('pending receipt and prepare never use the notification-only body shortcut', () => {
    const nodes = [article(payload, {id:'historical-match', role:'user'}),
        ...Array.from({length:12}, (_, i) => article('new', {id:'recent-' + i}))];
    const f = fixture({articles:nodes});
    const full = plain(f.call('read', '', {pendingText:payload}));
    const guarded = plain(f.call('read', '', {pendingText:payload, notificationOnly:true}));
    assert.deepEqual(guarded, full); assert.equal(guarded.diagnostics.fullMatches, 1);
    const prepared = f.call('prepare', payload, {notificationOnly:true});
    assert.equal(prepared.diagnostics.historicalBodiesSkipped, 0);
    assert.equal(prepared.beforeIds.length, 13);
});

test('notification polling sees streaming edits, append, reorder and page boundary immediately', () => {
    const nodes = Array.from({length:14}, (_, i) => article('text-' + i, {id:'n-' + i}));
    const f = fixture({articles:nodes});
    const compare = () => assert.deepEqual(plain(f.call('read', '', {notificationOnly:true})).messages,
        plain(f.call('read')).messages);
    compare(); nodes[13].querySelector('.message-content').innerText = 'stream changed'; compare();
    f.nodes.push(article('newest', {id:'newest'})); compare(); f.nodes.reverse(); compare();
    assert.equal(f.call('read', '', {notificationOnly:true}).messages.at(-1).id, 'n-0');
    const other = fixture({origin:'https://chatgpt.com.evil.test', articles:nodes});
    assert.equal(other.call('read', '', {notificationOnly:true}).state, 'page_required');
    assert.deepEqual(other.stats(), {clicks:0, insertions:0});
});
function simulateLayoutSkipped(root) {
    // Contract reproduction only. This getter override does not establish which CSS caused the live symptom.
    root.forceInnerTextEmpty = true;
    for (const child of root.children) simulateLayoutSkipped(child);
    return root;
}

test('rejects non-Dot pages and lookalike origins without touching input', () => {
    for (const options of [{origin:'http://chatgpt.com'}, {origin:'https://chatgpt.com.evil.test'}, {path:'/c/other'}, {path:'/dots/other'}]) {
        const f = fixture(options); assert.equal(f.call('prepare').state, 'page_required'); assert.equal(f.stats().insertions, 0);
    }
});
test('ambiguous input blocks preparation', () => assert.equal(fixture({count:2}).prepare().state, 'page_required'));
test('read is side-effect free', () => {
    const f = fixture({articles:[safeArticle()]}); assert.equal(f.call('read').state, 'ready');
    assert.deepEqual(f.stats(), {clicks:0, insertions:0});
});
test('supported message-row/self and body shapes exclude actions outside the selected body', () => {
    for (const role of ['user','assistant']) for (const body of ['message-content','message-body']) {
        const result = fixture({articles:[article('正文 fixture', {role, body})]}).call('read');
        const message = result.messages[0];
        assert.equal(message.role, role); assert.equal(message.text, '正文 fixture'); assert.equal(message.confirmable, true);
        assert.equal(message.roleSource, role === 'user' ? 'message-row.self' : 'message-row');
        assert.equal(message.textSource, body); assert.equal(result.diagnostics.baselineComplete, true);
    }
});
test('observed live nesting permits empty icon actions without rewriting body text', () => {
    for (const actionContainer of ['class','attribute']) {
        const node = liveArticle(payload, {actionContainer});
        const original = node.querySelector('.message-body').innerText;
        const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
        assert.equal(result.messages[0].text, original); assert.equal(result.messages[0].textSource, 'message-body');
        assert.equal(result.messages[0].confirmable, true); assert.equal(result.diagnostics.windowMatches, 1);
        assert.equal(result.diagnostics.emptyActionContainers, 1); assert.equal(result.diagnostics.nonemptyActionContainers, 0);
        assert.equal(result.diagnostics.ignoredEmptyIconControls, 1); assert.equal(result.diagnostics.unsafeControls, 0);
        assert.equal(node.querySelector('.message-body').innerText, original);
    }
});
test('unique body wins over outer content decorations rather than including metadata', () => {
    const result = fixture({articles:[liveArticle(payload, {outerDecoration:'OUTSIDE-BODY-METADATA'})]}).call('read');
    assert.equal(result.messages[0].text, payload); assert.equal(result.messages[0].confirmable, true);
    assert.equal(result.messages[0].textSource, 'message-body');
});
test('text-bearing inline actions never become a receipt after stripping display text', () => {
    const node = liveArticle(payload, {actionText:'Copy'});
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].text, payload); assert.equal(result.messages[0].confirmable, false);
    assert.equal(result.messages[0].textSource, 'body-with-controls'); assert.equal(result.diagnostics.nonemptyActionContainers, 1);
    assert.equal(result.diagnostics.unsafeControls, 1); assert.equal(result.diagnostics.newWindowMatches, 0);
    assert.ok(node.innerText.includes('Copy'));
});
test('empty icon outside a recognized inline action container remains unsafe', () => {
    const result = fixture({articles:[liveArticle(payload, {actionContainer:'none'})]}).call('read');
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.diagnostics.emptyActionContainers, 0);
    assert.equal(result.diagnostics.ignoredEmptyIconControls, 0); assert.equal(result.diagnostics.unsafeControls, 1);
});
test('all blocks inside a unique body are retained so a matching first block is not a receipt', () => {
    const node = liveArticle('', {bodyChildren:[new Element('p', {text:'First block\n'}), new Element('p', {text:'Second block'})]});
    const result = fixture({articles:[node]}).call('read', '', {pendingText:'First block', beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].text, 'First block\nSecond block'); assert.equal(result.diagnostics.windowMatches, 0);
    assert.equal(result.messages[0].confirmable, true);
});
test('multiple message bodies are ambiguous even when the first body matches', () => {
    const result = fixture({articles:[liveArticle(payload, {secondBody:true})]}).call('read', '',
        {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.messages[0].textSource, 'ambiguous-body');
    assert.equal(result.diagnostics.newWindowMatches, 0);
});
test('simulated blank innerText retains canonical DOM body and does not drop its stable row', () => {
    const node = simulateLayoutSkipped(liveArticle(payload));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages.length, 1); assert.equal(result.messages[0].id, 'live-message');
    assert.equal(result.messages[0].role, 'user'); assert.ok(result.messages[0].time !== undefined);
    assert.equal(result.messages[0].text, payload); assert.equal(result.messages[0].textSource, 'message-body-dom');
    assert.equal(result.messages[0].confirmable, true); assert.equal(result.diagnostics.newWindowMatches, 1);
    assert.equal(result.diagnostics.rows[0].innerTextLength, 0); assert.ok(result.diagnostics.rows[0].textContentLength > 0);
    assert.equal(result.diagnostics.rows[0].domTraversalSafe, true); assert.equal(result.diagnostics.rows[0].domFallbackUsed, true);
});
test('simulated blank innerText with content-visibility metadata uses no layout rectangles', () => {
    const node = simulateLayoutSkipped(liveArticle(payload));
    node.querySelector('.message-body').css = {contentVisibility:'auto'};
    const result = fixture({articles:[node]}).call('read');
    assert.equal(result.messages[0].text, payload); assert.equal(result.messages[0].confirmable, true);
    assert.equal(result.diagnostics.domFallbacks, 1);
});
test('DOM traversal preserves inline concatenation, block boundaries and explicit breaks', () => {
    const blocks = [new Element('p', {children:[new Element('span', {text:'hello'}), new Element('strong', {text:'world'})]}),
        new Element('p', {children:[new Element('span', {text:'next'}), new Element('br'), new Element('span', {text:'line'})]})];
    const node = simulateLayoutSkipped(liveArticle('', {bodyChildren:blocks}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:'helloworld\nnext\nline', beforeIds:[], baselineComplete:true});
    assert.equal(normalizeFixtureText(result.messages[0].text), 'helloworld next line');
    assert.ok(!result.messages[0].text.includes('hello world')); assert.equal(result.diagnostics.newWindowMatches, 1);
    assert.equal(result.messages[0].confirmable, true);
});
test('DOM traversal excludes explicitly hidden child text and hidden controls', () => {
    const hiddenByStyle = new Element('button', {text:'Hidden control label'}); hiddenByStyle.css = {display:'none'};
    const hiddenByVisibility = new Element('span', {text:'Hidden visibility'}); hiddenByVisibility.css = {visibility:'hidden'};
    const blocks = [new Element('span', {text:payload}), new Element('span', {text:'Hidden attribute', attrs:{hidden:''}}),
        new Element('span', {text:'Hidden ARIA', attrs:{'aria-hidden':'true'}}), hiddenByStyle, hiddenByVisibility];
    const node = simulateLayoutSkipped(liveArticle('', {bodyChildren:blocks}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].text, payload); assert.equal(result.messages[0].confirmable, true);
    assert.equal(result.diagnostics.rows[0].excludedHidden, 4); assert.equal(result.diagnostics.unsafeControls, 0);
});
test('DOM traversal keeps adjacent table cells separate rather than confirming concatenated text', () => {
    const a = new Element('td', {text:'A'}); a.css = {display:'table-cell'};
    const b = new Element('td', {text:'B'}); b.css = {display:'table-cell'};
    const row = new Element('tr', {children:[a,b]}); row.css = {display:'table-row'};
    const table = new Element('table', {children:[row]}); table.css = {display:'table'};
    const node = simulateLayoutSkipped(liveArticle('', {bodyChildren:[table]}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:'AB', beforeIds:[], baselineComplete:true});
    assert.equal(normalizeFixtureText(result.messages[0].text), 'A B');
    assert.equal(result.messages[0].confirmable, true);
    assert.equal(result.diagnostics.newWindowMatches, 0);
});
test('text-bearing action labels remain unsafe even when their innerText is also blank', () => {
    const node = simulateLayoutSkipped(liveArticle(payload, {actionText:'Copy label'}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.diagnostics.newWindowMatches, 0);
    assert.equal(result.diagnostics.emptyActionContainers, 0); assert.equal(result.diagnostics.nonemptyActionContainers, 1);
    assert.equal(result.diagnostics.rows[0].domTraversalSafe, false); assert.equal(result.diagnostics.unsafeControls, 1);
    assert.ok(node.textContent.includes('Copy label'));
});
test('unknown visible controls and unknown authored elements cannot confirm via DOM traversal', () => {
    const custom = new Element('custom-element', {text:'Unknown payload'});
    for (const node of [simulateLayoutSkipped(liveArticle(payload, {actionContainer:'none'})),
        simulateLayoutSkipped(liveArticle('', {bodyChildren:[new Element('span', {text:payload}), custom]}))]) {
        const result = fixture({articles:[node]}).call('read');
        assert.equal(result.messages[0].confirmable, false); assert.ok(result.diagnostics.rows[0].unsupportedNodes > 0);
    }
});
test('empty action text cannot conceal input values or unknown interactive roles', () => {
    for (const control of [new Element('input', {attrs:{value:payload}}), new Element('textarea'), new Element('select'),
        new Element('div', {attrs:{contenteditable:'true'}}), new Element('span', {attrs:{role:'combobox'}})]) {
        const node = liveArticle(payload);
        const actions = node.querySelector('.message-inline-actions');
        actions.children.push(control); control.parent = actions;
        simulateLayoutSkipped(node);
        const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
        assert.equal(result.messages[0].confirmable, false);
        assert.equal(result.diagnostics.newWindowMatches, 0);
    }
});
test('authored-empty body stays empty and returns its ID role and time without copying pending text', () => {
    const node = simulateLayoutSkipped(liveArticle(''));
    node.children.push(new Element('time', {attrs:{datetime:'2026-10-23T09:45:00Z'}}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages.length, 1); assert.equal(result.messages[0].id, 'live-message');
    assert.equal(result.messages[0].time, '2026-10-23T09:45:00Z'); assert.equal(result.messages[0].role, 'user');
    assert.equal(result.messages[0].text, ''); assert.equal(result.messages[0].confirmable, false);
    assert.equal(result.diagnostics.emptyBodies, 1); assert.equal(result.diagnostics.windowMatches, 0);
});
test('hidden-only authored body is not replaced with pending text', () => {
    const node = simulateLayoutSkipped(liveArticle('', {bodyChildren:[new Element('span', {text:payload, attrs:{hidden:''}})]}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].text, ''); assert.equal(result.messages[0].confirmable, false);
    assert.equal(result.diagnostics.rows[0].textContentMatchesPending, true); assert.equal(result.diagnostics.windowMatches, 0);
});
test('DOM traversal node budget stops confirmation instead of truncating into a matching payload', () => {
    const children = [new Element('span', {text:payload}), ...Array.from({length:8200}, () => new Element('span'))];
    const node = simulateLayoutSkipped(liveArticle('', {bodyChildren:children}));
    const result = fixture({articles:[node]}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.diagnostics.rows[0].domScanTruncated, true);
    assert.equal(result.diagnostics.domScanTruncatedCount, 1);
});
test('nested explicit stable ID and explicit descendant role are recognized without inventing IDs', () => {
    const f = fixture({articles:[article('User body', {id:'nested-id', role:null, nestedId:true, attributeRole:'user', roleAttributeOnBody:true})]});
    const message = f.call('read').messages[0];
    assert.equal(message.id, 'nested-id'); assert.equal(message.idSource, 'descendant');
    assert.equal(message.role, 'user'); assert.equal(message.roleSource, 'descendant-attribute'); assert.equal(message.confirmable, true);
});
test('missing stable ID fails receipt preflight before insertion', () => {
    const f = fixture({articles:[article('Body', {id:null, role:'user'})]});
    assert.equal(f.call('read').messages[0].confirmable, false); assert.equal(f.prepare().state, 'receipt_unavailable');
    assert.deepEqual(f.stats(), {clicks:0, insertions:0});
});
test('conflicting stable IDs fail closed', () => {
    const f = fixture({articles:[article('Body', {id:'outer', conflictId:'different-inner', role:'user'})]});
    const result = f.call('read'); assert.equal(result.messages[0].idSource, 'conflict');
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.diagnostics.idConflicts, 1);
    assert.equal(f.prepare().state, 'receipt_unavailable'); assert.equal(f.stats().insertions, 0);
});
test('unknown and conflicting historical roles cannot confirm but preserve a stable ID baseline', () => {
    for (const node of [article('Body', {role:null}), article('Body', {role:'user', attributeRole:'assistant'})]) {
        const f = fixture({articles:[node]}); const message = f.call('read').messages[0];
        assert.equal(message.role, 'unknown'); assert.equal(message.confirmable, false); assert.equal(f.prepare().state, 'prepared');
    }
});
test('body fallback remains readable but cannot confirm', () => {
    const f = fixture({articles:[article('Body', {body:null})]});
    const result = f.call('read'); assert.equal(result.messages[0].confirmable, false);
    assert.equal(result.diagnostics.unsafeBodies, 1); assert.equal(f.prepare().state, 'prepared');
});
test('multiple content roots cannot turn a matching prefix into confirmation', () => {
    const node = article(payload, {role:'user'});
    node.querySelector('.message-bubble-wrap').children.push(new Element('div', {text:'Additional actual message text', classes:['message-content']}));
    const f = fixture({articles:[node]}); const result = f.call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.messages[0].textSource, 'ambiguous-body');
    assert.equal(f.prepare().state, 'prepared');
});
test('controls mixed inside content are removed only from a detached display copy and stay unsafe', () => {
    const node = article('Actual text', {controlsInside:true}); const f = fixture({articles:[node]});
    const result = f.call('read'); assert.equal(result.messages[0].text, 'Actual text');
    assert.equal(result.messages[0].textSource, 'body-with-controls'); assert.equal(result.messages[0].confirmable, false);
    assert.ok(node.innerText.includes('COPY-CONTROL')); assert.equal(f.prepare().state, 'prepared');
});
test('bounds normal history to last twelve and text to 8000 without confirming truncated prefixes', () => {
    const f = fixture({articles:Array.from({length:40}, (_, i) => article('x'.repeat(10000), {id:'message-' + i}))});
    const result = f.call('read', '', {pendingText:'x'.repeat(8000), beforeIds:[], baselineComplete:true});
    assert.equal(result.messages.length, 12); assert.equal(result.messages[0].id, 'message-28');
    assert.ok(result.messages.every(message => message.text.length === 8000 && !message.confirmable));
    assert.equal(result.diagnostics.unsafeBodies, 40);
});
test('old same-text message outside the window is diagnostic only', () => {
    const nodes = [article(payload, {id:'old-match', role:'user'}), ...Array.from({length:12}, (_, i) => safeArticle('Other', 'other-' + i))];
    const result = fixture({articles:nodes}).call('read', '', {pendingText:payload, beforeIds:['old-match'], baselineComplete:true});
    assert.equal(result.diagnostics.fullMatches, 1); assert.equal(result.diagnostics.windowMatches, 0);
    assert.ok(result.messages.every(message => message.id !== 'old-match')); assert.equal(result.diagnostics.newWindowMatches, 0);
});
test('reverse DOM order does not silently widen the confirmation window', () => {
    const nodes = [article(payload, {id:'newest-at-start', role:'user'}), ...Array.from({length:20}, (_, i) => safeArticle('Older', 'older-' + i))];
    const result = fixture({articles:nodes}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.diagnostics.fullMatches, 1); assert.equal(result.diagnostics.windowMatches, 0);
    assert.ok(result.messages.every(message => message.id !== 'newest-at-start'));
});
test('virtualized absent message stays unknown and read performs no scroll or send', () => {
    const f = fixture({articles:[safeArticle()]});
    const result = f.call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.diagnostics.fullMatches, 0); assert.equal(result.diagnostics.windowMatches, 0);
    assert.deepEqual(f.stats(), {clicks:0, insertions:0});
});
test('duplicate ID outside normal window makes the returned occurrence nonconfirmable', () => {
    const nodes = [article('Earlier', {id:'duplicate'}), ...Array.from({length:12}, (_, i) => safeArticle('Other', 'other-' + i)),
        article(payload, {id:'duplicate', role:'user'})];
    const result = fixture({articles:nodes}).call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.diagnostics.duplicateIds, 2); assert.equal(result.diagnostics.baselineComplete, false);
    assert.equal(result.messages.at(-1).confirmable, false);
});
test('old same-text ID in baseline is not reported as a new window candidate', () => {
    const result = fixture({articles:[article(payload, {id:'existing', role:'user'})]}).call('read', '',
        {pendingText:payload, beforeIds:['existing'], baselineComplete:true});
    assert.equal(result.diagnostics.windowMatches, 1); assert.equal(result.diagnostics.newWindowMatches, 0);
    assert.equal(result.diagnostics.rows[0].inBefore, true);
});
test('multiple distinct same-text candidates are counted rather than guessed', () => {
    const result = fixture({articles:[article(payload, {id:'one', role:'user'}), article(payload, {id:'two', role:'user'})]}).call('read', '',
        {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.diagnostics.newWindowMatches, 2);
});
test('prepare captures all rendered stable IDs before insertion rather than only twelve', () => {
    const f = fixture({articles:Array.from({length:20}, (_, i) => safeArticle('History', 'history-' + i)),
        onInsert:nodes => nodes.push(article('Arrived during insertion', {id:'later'}))});
    const prepared = f.prepare(); assert.equal(prepared.state, 'prepared'); assert.equal(prepared.baselineComplete, true);
    assert.equal(prepared.beforeIds.length, 20); assert.ok(prepared.beforeIds.includes('history-0'));
    assert.ok(!prepared.beforeIds.includes('later')); assert.equal(f.stats().clicks, 0);
});
test('submit synchronously merges messages arriving during the prepare-submit delay into baseline', () => {
    const f = fixture({articles:[safeArticle()]}); const prepared = f.prepare();
    f.nodes.push(article(payload, {id:'arrived-before-click', role:'user'}));
    const result = f.submit(prepared); assert.equal(result.state, 'dispatched');
    assert.deepEqual(plain(result.beforeIds), ['old','arrived-before-click']); assert.equal(f.stats().clicks, 1);
});
test('never overwrites an existing webpage draft', () => {
    const f = fixture({draft:'Existing webpage draft', articles:[safeArticle()]});
    assert.equal(f.prepare().state, 'draft_present'); assert.equal(f.editor.innerText, 'Existing webpage draft'); assert.equal(f.stats().insertions, 0);
});
test('submit requires exact unchanged draft', () => {
    const f = fixture(); const prepared = f.prepare(); f.editor.innerText = 'Changed draft';
    assert.equal(f.submit(prepared).state, 'not_sent'); assert.equal(f.stats().clicks, 0);
});
test('one dispatched click cannot duplicate after the draft clears', () => {
    const f = fixture(); const prepared = f.prepare(); assert.equal(f.submit(prepared).state, 'dispatched');
    assert.equal(f.submit(prepared).state, 'not_sent'); assert.equal(f.stats().clicks, 1);
});
test('disabled send and busy page never dispatch', () => {
    const f = fixture(); const prepared = f.prepare(); f.send.disabled = true;
    assert.equal(f.submit(prepared).state, 'not_sent'); assert.equal(f.stats().clicks, 0);
    const b = fixture({busy:true}); assert.equal(b.prepare().state, 'receiving'); assert.equal(b.stats().insertions, 0);
});
test('rejects empty or oversized payload and failed insertion without sending', () => {
    const f = fixture(); for (const text of ['', '  ', 'x'.repeat(8001)]) assert.equal(f.call('prepare', text).state, 'invalid_text');
    const failed = fixture({insertFails:true}); assert.equal(failed.prepare().state, 'input_failed'); assert.equal(failed.stats().clicks, 0);
});
test('multiline and script-looking payload remain text', () => {
    const f = fixture(); const value = '<script>alert(1)</script>\n第二行';
    assert.equal(f.prepare(value).state, 'prepared'); assert.equal(f.editor.innerText, value); assert.equal(f.stats().clicks, 0);
});
test('submit refuses missing incomplete duplicate or malformed preparation baseline', () => {
    for (const probe of [{}, {beforeIds:[], baselineComplete:false}, {beforeIds:['same','same'], baselineComplete:true},
        {beforeIds:[null], baselineComplete:true}, {beforeIds:['x'.repeat(257)], baselineComplete:true}]) {
        const f = fixture(); f.prepare(); assert.equal(f.call('submit', payload, probe).state, 'receipt_unavailable');
        assert.equal(f.stats().clicks, 0); assert.equal(f.editor.innerText, payload);
    }
});
test('oversized rendered IDs cannot confirm or pass receipt preflight', () => {
    const f = fixture({articles:[article(payload, {id:'x'.repeat(257), role:'user'})]});
    const result = f.call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(result.messages[0].confirmable, false); assert.equal(result.diagnostics.baselineComplete, false);
    assert.equal(result.diagnostics.oversizedIds, 1); assert.equal(f.prepare().state, 'receipt_unavailable');
    assert.deepEqual(f.stats(), {clicks:0, insertions:0});
});
test('newly incomplete current DOM prevents submit before click and preserves draft', () => {
    const f = fixture({articles:[safeArticle()]}); const prepared = f.prepare();
    f.nodes.push(article('Unidentified new row', {id:null}));
    assert.equal(f.submit(prepared).state, 'receipt_unavailable'); assert.equal(f.stats().clicks, 0); assert.equal(f.editor.innerText, payload);
});
test('more than 2048 rendered rows prevents preparation and reports truncated scan', () => {
    const f = fixture({articles:Array.from({length:2049}, (_, i) => safeArticle('History', 'id-' + i))});
    const result = f.prepare(); assert.equal(result.state, 'receipt_unavailable'); assert.equal(result.diagnostics.totalArticles, 2049);
    assert.equal(result.diagnostics.scanTruncated, true); assert.equal(result.baselineComplete, false); assert.equal(f.stats().insertions, 0);
    f.nodes[2048] = article(payload, {role:'user', id:'new-user'});
    const read = f.call('read', '', {pendingText:payload, beforeIds:[], baselineComplete:true});
    assert.equal(read.diagnostics.newWindowMatches, 0);
    assert.equal(read.messages.at(-1).confirmable, false);
});
test('merged baseline above 2048 IDs prevents click even when each snapshot fits', () => {
    const f = fixture({articles:Array.from({length:2048}, (_, i) => safeArticle('History', 'id-' + i))});
    const prepared = f.prepare(); assert.equal(prepared.state, 'prepared');
    f.nodes.shift(); f.nodes.push(safeArticle('New arrival', 'new-id'));
    const result = f.submit(prepared); assert.equal(result.state, 'receipt_unavailable'); assert.equal(result.baselineComplete, false);
    assert.equal(f.stats().clicks, 0); assert.ok(result.beforeIds.length <= 2048);
});
test('exception after dispatch stays exceptional and never triggers a retry', () => {
    const f = fixture({clickThrows:true}); const prepared = f.prepare();
    assert.throws(() => f.submit(prepared), /result lost after click/); assert.equal(f.stats().clicks, 1);
});
test('diagnostics contain no payload or stable ID values', () => {
    const secretText = 'PRIVATE-FIXTURE-TEXT'; const secretId = 'PRIVATE-STABLE-ID';
    const result = fixture({articles:[article(secretText, {id:secretId, role:'user'})]}).call('read', '',
        {pendingText:secretText, beforeIds:[secretId], baselineComplete:true});
    const diagnostics = JSON.stringify(result.diagnostics);
    assert.ok(!diagnostics.includes(secretText)); assert.ok(!diagnostics.includes(secretId));
    assert.equal(result.diagnostics.fullMatches, 1); assert.equal(result.diagnostics.probeBaselineComplete, true);
});
