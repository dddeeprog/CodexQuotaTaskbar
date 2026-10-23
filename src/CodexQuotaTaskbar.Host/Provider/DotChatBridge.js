// DOM-only adapter. Reads public rendered elements; never page stores, tokens, or private APIs.
((action, text, probe = {}) => {
    const limit = 2048;
    if (!probe || typeof probe !== 'object') probe = {};
    const normalize = value => value.trim().replace(/\s+/gu, ' ');
    const pending = typeof probe.pendingText === 'string' && probe.pendingText.trim() ? normalize(probe.pendingText) : null;
    const suppliedIds = Array.isArray(probe.beforeIds) ? probe.beforeIds : [];
    const suppliedValid = Array.isArray(probe.beforeIds) && suppliedIds.length <= limit &&
        suppliedIds.every(id => typeof id === 'string' && id.trim() && id.length <= 256) && new Set(suppliedIds).size === suppliedIds.length;
    const before = new Set(suppliedValid ? suppliedIds : []);
    const emptyDiagnostics = {baselineComplete:false, totalArticles:0, returnedMessages:0, missingIds:0,
        duplicateIds:0, unknownRoles:0, unsafeBodies:0, fullMatches:0, windowMatches:0,
        editors:0, sends:0, busy:false, scanTruncated:false};
    if (location.origin !== 'https://chatgpt.com' || location.pathname !== '/dots/home')
        return {state:'page_required', messages:[], diagnostics:emptyDiagnostics};

    const visible = element => element.getClientRects().length > 0;
    const editors = [...document.querySelectorAll('[role="textbox"][contenteditable="true"]')].filter(visible);
    const buttons = [...document.querySelectorAll('button')].filter(visible);
    const sends = buttons.filter(button => /^(发送|Send message|Send)$/.test(button.getAttribute('aria-label') || button.innerText.trim()));
    const busy = buttons.some(button => /^(停止|停止生成|停止回复|Stop|Stop generating|Stop streaming)$/.test(button.getAttribute('aria-label') || button.innerText.trim()));
    const articles = [...document.querySelectorAll('article')];
    const scanned = articles.slice(-limit);
    const windowStart = Math.max(0, scanned.length - 12);
    const controls = 'button, [role="button"], time, .message-actions, .message-inline-actions, [data-message-actions]';
    const findAll = (root, name) => [...(root.classList?.contains(name) ? [root] : []), ...root.querySelectorAll('.' + name)];
    const domBudget = {remaining:32768};
    const inlineTags = new Set('A ABBR B BDI BDO CITE CODE DEL EM I INS KBD MARK Q S SAMP SMALL SPAN STRONG SUB SUP U VAR WBR'.split(' '));
    const blockTags = new Set('ADDRESS ARTICLE ASIDE BLOCKQUOTE DD DIV DL DT FIGCAPTION FIGURE FOOTER H1 H2 H3 H4 H5 H6 HEADER LI MAIN NAV OL P PRE SECTION TABLE TBODY TD TFOOT TH THEAD TR UL'.split(' '));
    const nonContentTags = new Set(['SCRIPT','STYLE','TEMPLATE','NOSCRIPT']);
    const readDomBody = (root, actionContainers, emptyActions) => {
        const state = {excludedHidden:0, excludedActions:0, unsupportedNodes:0, unsafeControls:0, domScanTruncated:false};
        const pieces = []; let visited = 0, characters = 0;
        const append = value => {
            if (characters + value.length > 16000) { state.domScanTruncated = true; return; }
            characters += value.length; pieces.push(value);
        };
        const visit = node => {
            if (++visited > 8192 || --domBudget.remaining < 0) { state.domScanTruncated = true; return; }
            if (node.nodeType === 3) { append(node.nodeValue || ''); return; }
            if (node.nodeType !== 1) return;
            let style;
            try { style = typeof getComputedStyle === 'function' ? getComputedStyle(node) : null; } catch { style = null; }
            if (!style) { state.unsupportedNodes++; return; }
            if (node.getAttribute('hidden') !== null || node.getAttribute('aria-hidden') === 'true' ||
                style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') {
                state.excludedHidden++; return;
            }
            const tag = node.tagName;
            if (nonContentTags.has(tag)) return;
            if (actionContainers.includes(node)) {
                state.excludedActions++;
                if (!emptyActions.has(node)) { state.unsupportedNodes++; state.unsafeControls++; }
                return;
            }
            if (tag === 'BUTTON' || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' ||
                node.getAttribute('role') === 'button' || node.getAttribute('contenteditable') === 'true') {
                state.unsupportedNodes++; state.unsafeControls++; return;
            }
            if (tag === 'BR') { append('\n'); return; }
            if (tag === 'HR') { append('\n'); return; }
            if (!inlineTags.has(tag) && !blockTags.has(tag)) state.unsupportedNodes++;
            // Layout containment/content-visibility may suppress innerText. It does not change authored text.
            // Do not inspect rectangles or invent whitespace between inline text nodes.
            const block = ['block','flex','grid','list-item','table','table-row','table-cell','table-caption'].includes(style.display) ||
                (!style.display && blockTags.has(tag));
            if (block) append('\n');
            if (!node.childNodes) { state.unsupportedNodes++; return; }
            for (const child of node.childNodes) {
                visit(child);
                if (state.domScanTruncated) break;
            }
            if (block) append('\n');
        };
        visit(root);
        return {text:pieces.join('').trim(), safe:state.unsupportedNodes === 0 && !state.domScanTruncated, ...state};
    };
    const records = scanned.map((article, index) => {
        const idNodes = [article, ...article.querySelectorAll('[data-message-id]')];
        const ids = [...new Set(idNodes.map(node => node.getAttribute('data-message-id')).filter(id => typeof id === 'string' && id.trim()))];
        const id = ids.length === 1 ? ids[0] : '';
        const idSource = ids.length > 1 ? 'conflict' : !id ? 'missing' :
            article.getAttribute('data-message-id') === id ? 'article' : 'descendant';
        const roleNodes = [article, ...article.querySelectorAll('[data-message-author-role]')];
        const attributeRoles = [...new Set(roleNodes.map(node => node.getAttribute('data-message-author-role')).filter(Boolean))];
        const rows = [...(article.classList?.contains('message-row') ? [article] : []), ...article.querySelectorAll('.message-row')];
        const rowRoles = [...new Set(rows.map(row => row.classList.contains('self') ? 'user' : 'assistant'))];
        const roles = [...new Set([...attributeRoles, ...rowRoles])];
        const role = roles.length === 1 && ['user','assistant'].includes(roles[0]) ? roles[0] : 'unknown';
        const roleSource = role === 'unknown' ? roles.length > 1 ? 'conflict' : 'unknown' : attributeRoles.length ?
            article.getAttribute('data-message-author-role') ? 'article-attribute' : 'descendant-attribute' :
            role === 'user' ? 'message-row.self' : 'message-row';
        const bubbles = findAll(article, 'message-bubble-wrap');
        const bubble = bubbles[0];
        const bodyRoot = bubble || article;
        const contents = findAll(bodyRoot, 'message-content');
        const bodies = findAll(bodyRoot, 'message-body');
        const content = contents[0];
        const body = bodies[0] || content;
        const ambiguousBody = !!body && (bubbles.length !== 1 || bodies.length > 1 || (!bodies.length && contents.length !== 1));
        const textNode = body || bubble || article;
        let textSource = bodies.length ? 'message-body' : content ? 'message-content' : bubble ? 'bubble-fallback' : 'article-fallback';
        const actionContainers = [...textNode.querySelectorAll('.message-inline-actions, [data-message-actions]')];
        // innerText alone may be blank because the whole message has skipped layout.
        // Only authored-empty operation containers can be omitted from a canonical text traversal.
        const emptyActions = new Set(actionContainers.filter(container =>
            !container.innerText.trim() && !String(container.textContent || '').trim() &&
            ![...container.querySelectorAll('input, textarea, select, [contenteditable], [role]')].some(control =>
                ['INPUT','TEXTAREA','SELECT'].includes(control.tagName) || control.getAttribute('contenteditable') !== null ||
                !['button','presentation','none','img'].includes(control.getAttribute('role')))));
        const controlNodes = [...textNode.querySelectorAll(controls)];
        const emptyIconControl = control => {
            if (control.tagName !== 'BUTTON' && control.getAttribute('role') !== 'button' && !actionContainers.includes(control)) return false;
            let container = control;
            while (container && container !== textNode && !actionContainers.includes(container)) container = container.parentElement;
            return container && container !== textNode && emptyActions.has(container) && textNode.contains(container);
        };
        const hasUnsafeControls = controlNodes.some(control => !emptyIconControl(control));
        const ignoredEmptyIconControls = controlNodes.filter(control =>
            (control.tagName === 'BUTTON' || control.getAttribute('role') === 'button') && emptyIconControl(control)).length;
        const rawInnerText = String(textNode.innerText || '').trim();
        const rawTextContent = String(textNode.textContent || '').trim();
        let dom = {text:'', safe:false, excludedHidden:0, excludedActions:0, unsupportedNodes:0, unsafeControls:0, domScanTruncated:false};
        let domFallbackUsed = false;
        let renderedText;
        if (!rawInnerText && body && !ambiguousBody) {
            dom = readDomBody(body, actionContainers, emptyActions);
            renderedText = dom.text;
            domFallbackUsed = !!dom.text;
            if (dom.safe && dom.text) textSource = bodies.length ? 'message-body-dom' : 'message-content-dom';
            else if (!dom.safe) textSource = 'body-with-controls';
        } else if (hasUnsafeControls) {
            // Detached copy only; do not modify the webpage or guess a confirmation from rewritten text.
            const copy = textNode.cloneNode(true);
            for (const control of copy.querySelectorAll(controls)) control.remove();
            renderedText = copy.textContent;
            textSource = 'body-with-controls';
        } else renderedText = rawInnerText;
        const unboundedText = String(renderedText || '').trim();
        const value = unboundedText.slice(0, 8000);
        const unsafeBody = !body || (!rawInnerText ? !dom.safe : hasUnsafeControls) || ambiguousBody || unboundedText.length > 8000;
        if (ambiguousBody) textSource = 'ambiguous-body';
        if (unboundedText.length > 8000) textSource = 'truncated-body';
        const pendingMatch = pending !== null && normalize(value) === pending;
        return {id, role, text:index >= windowStart ? value : '', hasText:!!value,
            time:article.querySelector('time[datetime]')?.getAttribute('datetime') || '',
            idSource, roleSource, textSource, unsafeBody, pendingMatch, index,
            emptyActionContainers:emptyActions.size, nonemptyActionContainers:actionContainers.length - emptyActions.size,
            ignoredEmptyIconControls, unsafeControls:rawInnerText ? hasUnsafeControls : dom.unsafeControls > 0,
            innerTextLength:rawInnerText.length, textContentLength:rawTextContent.length, domTextLength:dom.text.length,
            domFallbackUsed, domTraversalSafe:dom.safe, excludedHidden:dom.excludedHidden, excludedActions:dom.excludedActions,
            unsupportedNodes:dom.unsupportedNodes, domScanTruncated:dom.domScanTruncated,
            textContentMatchesPending:pending !== null && normalize(rawTextContent) === pending};
    });
    const idCounts = new Map();
    for (const record of records) if (record.id) idCounts.set(record.id, (idCounts.get(record.id) || 0) + 1);
    for (const record of records) record.confirmable = articles.length <= limit && record.hasText && !!record.id && record.id.length <= 256 && idCounts.get(record.id) === 1 &&
        record.role !== 'unknown' && !record.unsafeBody;
    // Historical body/role uncertainty does not erase a stable identity from the exclusion baseline.
    const baselineComplete = articles.length <= limit && records.every(record =>
        !!record.id && record.id.length <= 256 && idCounts.get(record.id) === 1);
    const baselineIds = [...new Set(records.map(record => record.id).filter(id => id && id.length <= 256))];
    const messages = records.slice(-12).map(record => ({
        id:record.id, role:record.role, text:record.text, time:record.time,
        confirmable:record.confirmable, idSource:record.idSource, roleSource:record.roleSource, textSource:record.textSource
    }));
    const fullMatches = records.filter(record => record.pendingMatch);
    const windowMatches = fullMatches.filter(record => record.index >= windowStart);
    const rows = [...records.slice(-12), ...fullMatches.filter(record => record.index < windowStart).slice(0, 2)];
    const diagnostics = {baselineComplete, totalArticles:articles.length, returnedMessages:messages.length,
        missingIds:records.filter(record => record.idSource === 'missing' || record.idSource === 'conflict').length,
        duplicateIds:records.filter(record => record.id && idCounts.get(record.id) > 1).length,
        unknownRoles:records.filter(record => record.role === 'unknown').length,
        unsafeBodies:records.filter(record => record.unsafeBody).length,
        fullMatches:fullMatches.length, windowMatches:windowMatches.length,
        editors:editors.length, sends:sends.length, busy, scanTruncated:articles.length > limit,
        idConflicts:records.filter(record => record.idSource === 'conflict').length,
        oversizedIds:records.filter(record => record.id.length > 256).length,
        emptyActionContainers:records.reduce((count, record) => count + record.emptyActionContainers, 0),
        nonemptyActionContainers:records.reduce((count, record) => count + record.nonemptyActionContainers, 0),
        ignoredEmptyIconControls:records.reduce((count, record) => count + record.ignoredEmptyIconControls, 0),
        unsafeControls:records.filter(record => record.unsafeControls).length,
        emptyBodies:records.filter(record => !record.hasText).length,
        domFallbacks:records.filter(record => record.domFallbackUsed).length,
        domTraversalUnsafe:records.filter(record => record.innerTextLength === 0 && !record.domTraversalSafe).length,
        excludedHidden:records.reduce((count, record) => count + record.excludedHidden, 0),
        excludedActions:records.reduce((count, record) => count + record.excludedActions, 0),
        unsupportedNodes:records.reduce((count, record) => count + record.unsupportedNodes, 0),
        domScanTruncatedCount:records.filter(record => record.domScanTruncated).length,
        newWindowMatches:windowMatches.filter(record => record.confirmable && !before.has(record.id)).length,
        probeBaselineComplete:suppliedValid && probe.baselineComplete === true,
        rows:rows.map(record => ({domIndex:articles.length - scanned.length + record.index,
            role:record.role, idSource:record.idSource, roleSource:record.roleSource, textSource:record.textSource,
            idMissing:!record.id, idDuplicate:!!record.id && idCounts.get(record.id) > 1,
            confirmable:record.confirmable, pendingMatch:record.pendingMatch, inBefore:before.has(record.id),
            inWindow:record.index >= windowStart, emptyActionContainers:record.emptyActionContainers,
            nonemptyActionContainers:record.nonemptyActionContainers, ignoredEmptyIconControls:record.ignoredEmptyIconControls,
            unsafeControls:record.unsafeControls, innerTextLength:record.innerTextLength, textContentLength:record.textContentLength,
            domTextLength:record.domTextLength, domFallbackUsed:record.domFallbackUsed, domTraversalSafe:record.domTraversalSafe,
            excludedHidden:record.excludedHidden, excludedActions:record.excludedActions, unsupportedNodes:record.unsupportedNodes,
            domScanTruncated:record.domScanTruncated, textContentMatchesPending:record.textContentMatchesPending}))};
    const response = state => ({state, diagnostics});
    if (editors.length !== 1 || sends.length !== 1) return {...response(busy ? 'receiving' : 'page_required'), messages};
    if (action === 'read') return {...response(busy ? 'receiving' : 'ready'), messages, draftEmpty:!editors[0].innerText.trim()};
    if (typeof text !== 'string' || !text.trim() || text.length > 8000) return response('invalid_text');
    if (busy) return response('receiving');
    const editor = editors[0];
    if (action === 'prepare') {
        if (editor.innerText.trim()) return response('draft_present');
        if (!baselineComplete) return {...response('receipt_unavailable'), beforeIds:baselineIds, baselineComplete:false};
        const beforeIds = baselineIds;
        editor.focus();
        const inserted = document.execCommand('insertText', false, text);
        return {...response(inserted && editor.innerText.trim() === text.trim() ? 'prepared' : 'input_failed'), beforeIds, baselineComplete};
    }
    if (action === 'submit') {
        if (editor.innerText.trim() !== text.trim() || sends[0].disabled) return response('not_sent');
        const beforeIds = [...new Set([...before, ...baselineIds])];
        const complete = baselineComplete && suppliedValid && probe.baselineComplete === true && beforeIds.length <= limit;
        if (!complete) return {...response('receipt_unavailable'), beforeIds:beforeIds.slice(0, limit), baselineComplete:false};
        sends[0].click();
        // A dispatched click is not a delivery receipt. Exceptions here remain uncertain; never retry automatically.
        return {...response('dispatched'), beforeIds, baselineComplete:true};
    }
    return response('invalid_action');
})
