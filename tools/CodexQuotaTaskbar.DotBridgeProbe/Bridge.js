// DOM-only bridge: no credentials, network calls, page-internal stores or history export.
((TOKEN, ACTION) => {
    const visible = e => e.getClientRects().length > 0;
    const allowed = location.origin === 'https://chatgpt.com' && location.pathname === '/dots/home';
    if (!allowed) return {state: 'not_dot_page'};
    const editors = [...document.querySelectorAll('[role="textbox"][contenteditable="true"]')].filter(visible);
    const buttons = [...document.querySelectorAll('button')].filter(visible);
    const sendButtons = buttons.filter(b => /^(发送|Send message|Send)$/.test(b.getAttribute('aria-label') || b.innerText.trim()));
    const articles = [...document.querySelectorAll('article')].slice(-50);
    const knownConversation = articles.some(a => a.innerText.trim() === 'DOT-OK-4827');
    const replies = articles.filter(a => a.innerText.trim() === TOKEN);
    if (replies.length) return {state: 'reply_received', reply: TOKEN};
    if (!knownConversation) return {state: 'conversation_unverified', editorCount: editors.length};
    if (editors.length !== 1 || sendButtons.length !== 1) return {state: 'composer_unavailable', editorCount: editors.length, sendCount: sendButtons.length};
    const prompt = `这是额度岛 WebView2 桥接的一次连通性测试，不是工作任务。请只回复“${TOKEN}”，不要调用工具、创建任务或修改文件。`;
    if (ACTION === 'submit') {
        if (editors[0].innerText.trim() !== prompt || sendButtons[0].disabled) return {state: 'not_sent'};
        sendButtons[0].click();
        return {state: 'click_dispatched'};
    }
    if (ACTION !== 'send') return {state: 'ready'};
    if (articles.some(a => a.innerText.includes(TOKEN))) return {state: 'already_submitted'};
    const editor = editors[0];
    if (editor.innerText.trim()) return {state: 'draft_present'};
    editor.focus();
    const inserted = document.execCommand('insertText', false, prompt);
    if (!inserted || !editor.innerText.includes(TOKEN)) return {state: 'input_failed'};
    // Submission is a separate call after React has updated the Send button.
    return {state: 'draft_prepared'};
})
