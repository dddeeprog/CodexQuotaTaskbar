(() => {
    "use strict";

    const installationKey = "__codexQuotaSubscriptionReader";
    const previousInstallation = window[installationKey];
    if (typeof previousInstallation === "function") {
        previousInstallation();
    }

    if (location.origin !== "https://chatgpt.com" || !window.chrome?.webview) {
        return;
    }

    const bridge = window.chrome.webview;
    const maximumResponseBytes = 1024 * 1024;
    const controller = new AbortController();
    let disposed = false;
    let consumed = false;
    let timeout;

    const dispose = () => {
        disposed = true;
        bridge.removeEventListener("message", onMessage);
        clearTimeout(timeout);
        controller.abort();
        if (window[installationKey] === dispose) {
            delete window[installationKey];
        }
    };

    class QueryFailure extends Error {
        constructor(status) {
            super("Subscription query could not be completed.");
            this.status = status;
        }
    }

    const isObject = value => value !== null && typeof value === "object" && !Array.isArray(value);
    const own = (value, name) => isObject(value) && Object.hasOwn(value, name) ? value[name] : undefined;

    async function readJson(path, accessToken) {
        if (location.origin !== "https://chatgpt.com" || disposed) {
            throw new QueryFailure("failed");
        }
        const headers = { Accept: "application/json" };
        if (accessToken) {
            headers.Authorization = `Bearer ${accessToken}`;
        }
        const response = await fetch(`https://chatgpt.com${path}`, {
            method: "GET",
            credentials: "same-origin",
            redirect: "error",
            cache: "no-store",
            headers,
            signal: controller.signal,
        });
        if (response.status === 401) {
            throw new QueryFailure("auth_required");
        }
        if (response.status === 403 || response.headers.get("cf-mitigated") === "challenge") {
            throw new QueryFailure("verification_required");
        }
        if (!response.ok || response.redirected) {
            throw new QueryFailure("failed");
        }
        const contentType = response.headers.get("content-type") || "";
        if (!/^application\/(?:[a-z0-9!#$&^_.+-]+\+)?json(?:\s*;|\s*$)/i.test(contentType)) {
            throw new QueryFailure("failed");
        }
        const declaredLength = response.headers.get("content-length");
        if (declaredLength && /^\d+$/.test(declaredLength) && Number(declaredLength) > maximumResponseBytes) {
            throw new QueryFailure("failed");
        }
        if (!response.body || typeof response.body.getReader !== "function") {
            throw new QueryFailure("failed");
        }

        const reader = response.body.getReader();
        const chunks = [];
        let total = 0;
        try {
            while (true) {
                const { done, value } = await reader.read();
                if (done) {
                    break;
                }
                total += value.byteLength;
                if (total > maximumResponseBytes) {
                    await reader.cancel();
                    throw new QueryFailure("failed");
                }
                chunks.push(value);
            }
        } finally {
            reader.releaseLock();
        }
        const bytes = new Uint8Array(total);
        let offset = 0;
        for (const chunk of chunks) {
            bytes.set(chunk, offset);
            offset += chunk.byteLength;
        }
        const result = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
        if (!isObject(result) && !Array.isArray(result)) {
            throw new QueryFailure("failed");
        }
        return result;
    }

    const isGuid = value => /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value);
    const normalizeId = value => {
        if (typeof value !== "string" || !value.trim() || value.length > 256 || /[\u0000-\u001f\u007f]/.test(value)) return null;
        const trimmed = value.trim();
        return isGuid(trimmed) ? trimmed.toLowerCase() : trimmed;
    };

    function readAccountIdentity(record, key, expectedId, allowRootId = true) {
        const account = isObject(own(record, "account")) ? record.account : null;
        const nodes = account ? [account, record] : [record];
        const ids = new Set(nodes.flatMap(node => ["account_id", "chatgpt_account_id", "workspace_id"]
            .map(name => normalizeId(own(node, name)))).filter(Boolean));
        if (ids.size === 0) {
            const genericId = normalizeId(own(account, "id")) ?? (allowRootId ? normalizeId(own(record, "id")) : null);
            if (genericId) ids.add(genericId);
        }
        const mapId = normalizeId(key);
        // "default" and other display aliases are not identities. A canonical map key is.
        if (mapId && (isGuid(mapId) || mapId === expectedId)) ids.add(mapId);
        return { id: ids.size === 1 ? [...ids][0] : null, conflict: ids.size > 1 };
    }

    function collectAccounts(root) {
        const accounts = own(root, "accounts") ?? root;
        if (Array.isArray(accounts)) {
            return accounts.filter(isObject).map(record => ({ record, key: null }));
        }
        if (!isObject(accounts)) {
            return [];
        }
        if (["account", "entitlement", "account_id", "id", "chatgpt_account_id", "workspace_id"]
            .some(name => Object.hasOwn(accounts, name))) {
            return [{ record: accounts, key: null }];
        }
        return Object.entries(accounts).filter(([, record]) => isObject(record)).map(([key, record]) => ({ record, key }));
    }

    function readTimestamp(value) {
        let timestamp;
        if (typeof value === "number" && Number.isSafeInteger(value)) {
            timestamp = value >= 10_000_000_000 ? value : value * 1000;
        } else if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}(?:[Tt ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})?)?$/.test(value.trim())) {
            const raw = value.trim();
            timestamp = Date.parse(raw.length > 10 && !/(?:[Zz]|[+-]\d{2}:\d{2})$/.test(raw) ? `${raw}Z` : raw);
        } else {
            return null;
        }
        const date = new Date(timestamp);
        const now = Date.now();
        const maximumExpiration = new Date(now);
        const currentMonth = maximumExpiration.getUTCMonth();
        maximumExpiration.setUTCFullYear(maximumExpiration.getUTCFullYear() + 10);
        if (maximumExpiration.getUTCMonth() !== currentMonth) {
            maximumExpiration.setUTCDate(0);
        }
        return Number.isFinite(date.getTime()) && date.getTime() > now && date <= maximumExpiration
            ? date.toISOString()
            : null;
    }

    async function readSubscription(accountId) {
        const expectedId = normalizeId(accountId);
        if (!expectedId) throw new QueryFailure("account_unverified");
        const session = await readJson("/api/auth/session");
        const accessToken = own(session, "accessToken");
        if (typeof accessToken !== "string" || !accessToken.trim() || accessToken.length > 32_768) {
            throw new QueryFailure("auth_required");
        }
        const accounts = await readJson("/backend-api/accounts/check/v4-2023-04-27", accessToken);
        const candidates = collectAccounts(accounts).map(entry => ({ ...entry,
            identity: readAccountIdentity(entry.record, entry.key, expectedId),
        }));
        if (candidates.some(entry => entry.identity.conflict)) throw new QueryFailure("account_unverified");
        const matches = candidates.filter(entry => entry.identity.id === expectedId);
        if (matches.length === 0) {
            const verifiedDifferent = candidates.length > 0 && candidates.every(entry => entry.identity.id);
            throw new QueryFailure(verifiedDifferent ? "account_mismatch" : "account_unverified");
        }
        const expirations = new Set(matches.map(({ record }) => {
            const account = isObject(own(record, "account")) ? record.account : record;
            return readTimestamp(own(own(record, "entitlement"), "expires_at"))
                ?? readTimestamp(own(account, "expires_at"));
        }).filter(Boolean));
        if (expirations.size === 1) {
            return [...expirations][0];
        }

        const subscription = await readJson(`/backend-api/subscriptions?account_id=${encodeURIComponent(accountId)}`, accessToken);
        if (!isObject(subscription)) {
            throw new QueryFailure("failed");
        }
        const identity = readAccountIdentity(subscription, null, expectedId, false);
        if (identity.conflict) throw new QueryFailure("account_unverified");
        if (identity.id && identity.id !== expectedId) {
            throw new QueryFailure("account_mismatch");
        }
        return readTimestamp(own(subscription, "active_until"))
            ?? readTimestamp(own(subscription, "expires_at"));
    }

    function onMessage(event) {
        const request = event.data;
        if (consumed || disposed || location.origin !== "https://chatgpt.com"
            || !isObject(request) || request.kind !== "cqtb.readSubscription"
            || typeof request.nonce !== "string" || !request.nonce || request.nonce.length > 128) {
            return;
        }
        consumed = true;
        bridge.removeEventListener("message", onMessage);
        const nonce = request.nonce;
        const accountId = typeof request.accountId === "string" && request.accountId.length <= 256
            ? request.accountId.trim()
            : "";
        timeout = setTimeout(() => controller.abort(), 20_000);

        void (async () => {
            let status = "failed";
            let expiresAt = null;
            try {
                if (!accountId) {
                    throw new QueryFailure("account_unverified");
                }
                expiresAt = await readSubscription(accountId);
                status = expiresAt ? "ok" : "not_provided";
            } catch (error) {
                if (error instanceof QueryFailure) {
                    status = error.status;
                }
            } finally {
                clearTimeout(timeout);
            }
            try {
                if (!disposed && location.origin === "https://chatgpt.com") {
                    bridge.postMessage({ kind: "cqtb.subscription", nonce, status, accountId, expiresAt });
                }
            } catch {
                // The visible page or its host may have closed while the request was pending.
            } finally {
                dispose();
            }
        })();
    }

    window[installationKey] = dispose;
    bridge.addEventListener("message", onMessage);
})();
