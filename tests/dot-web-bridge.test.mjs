import {readFileSync} from 'node:fs';
import {runInNewContext} from 'node:vm';
import assert from 'node:assert/strict';
import test from 'node:test';

const source = readFileSync(new URL('../tools/CodexQuotaTaskbar.DotBridgeProbe/Bridge.js', import.meta.url), 'utf8');
function fixture({origin = 'https://chatgpt.com', path = '/dots/home', known = true, draft = '', reply = null, editors = 1} = {}) {
    let focused = 0, inserted = 0, clicks = 0;
    const editor = {innerText: draft, getClientRects: () => [1], focus: () => focused++};
    const button = {innerText: '', disabled: false, getClientRects: () => [1], getAttribute: () => '发送', click: () => clicks++};
    const articles = [...(known ? [{innerText: 'DOT-OK-4827'}] : []), ...(reply ? [{innerText: reply}] : [])];
    const bridge = runInNewContext(source, {
        location: {origin, pathname: path},
        document: {
            querySelectorAll: selector => selector === 'button' ? [button] : selector === 'article' ? articles : Array(editors).fill(editor),
            execCommand: (command, ui, value) => { inserted++; editor.innerText = value; return true; }
        }
    });
    return {call: action => bridge('DOT-WV-test', action), editor, button, articles, stats: () => ({focused, inserted, clicks})};
}

test('exact origin and Dot path required', () => {
    for (const options of [{origin: 'https://chatgpt.com.evil.test'}, {path: '/c/other'}]) {
        const f = fixture(options); assert.equal(f.call('send').state, 'not_dot_page');
        assert.deepEqual(f.stats(), {focused: 0, inserted: 0, clicks: 0});
    }
});
test('requires known conversation marker', () => {
    const f = fixture({known: false}); assert.equal(f.call('send').state, 'conversation_unverified'); assert.equal(f.stats().inserted, 0);
});
test('ambiguous composer is not used', () => {
    const f = fixture({editors: 2}); assert.equal(f.call('send').state, 'composer_unavailable');
});
test('read does not mutate the page', () => {
    const f = fixture(); assert.equal(f.call('read').state, 'ready'); assert.deepEqual(f.stats(), {focused: 0, inserted: 0, clicks: 0});
});
test('preserves existing user draft', () => {
    const f = fixture({draft: 'my draft'}); assert.equal(f.call('send').state, 'draft_present'); assert.equal(f.editor.innerText, 'my draft');
});
test('prepare and submit are separate and exact-draft guarded', () => {
    const f = fixture(); assert.equal(f.call('send').state, 'draft_prepared'); assert.equal(f.stats().clicks, 0);
    assert.equal(f.call('submit').state, 'click_dispatched'); assert.equal(f.stats().clicks, 1);
    f.editor.innerText = 'changed by user'; assert.equal(f.call('submit').state, 'not_sent'); assert.equal(f.stats().clicks, 1);
});
test('disabled send cannot submit', () => {
    const f = fixture(); f.call('send'); f.button.disabled = true; assert.equal(f.call('submit').state, 'not_sent');
});
test('submitted marker prevents preparing duplicate', () => {
    const f = fixture({reply: 'test message containing DOT-WV-test'}); assert.equal(f.call('send').state, 'already_submitted');
});
test('only exact response matches and returns no conversation history', () => {
    const f = fixture({reply: 'DOT-WV-test'}); const result = f.call('read'); assert.equal(result.state, 'reply_received');
    assert.deepEqual(Object.keys(result).sort(), ['reply', 'state']); assert.equal(result.reply, 'DOT-WV-test');
});
