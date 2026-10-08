import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../src/CodexQuotaTaskbar.Host/Provider/BrowserSubscriptionReader.js", import.meta.url), "utf8");
const accountId = "account-current";
const accessToken = "synthetic-secret-access-token";
const testNow = Date.parse("2026-10-08T08:00:00.000Z");
const futureExpiration = "2026-11-20T10:30:00.000Z";
const request = { kind: "cqtb.readSubscription", nonce: "synthetic-nonce", accountId };

function jsonResponse(body, options = {}) {
    return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" }, ...options });
}

function createHarness(responses = [], { origin = "https://chatgpt.com", now = testNow } = {}) {
    const listeners = new Set();
    const requests = [];
    const messages = [];
    const timers = new Map();
    let nextTimer = 0;
    let completion;
    const completed = new Promise(resolve => { completion = resolve; });
    const location = { origin };
    const bridge = {
        addEventListener: (name, listener) => { assert.equal(name, "message"); listeners.add(listener); },
        removeEventListener: (name, listener) => { assert.equal(name, "message"); listeners.delete(listener); },
        postMessage: message => {
            const copy = JSON.parse(JSON.stringify(message));
            messages.push(copy);
            completion(copy);
        },
    };
    const context = vm.createContext({
        window: { chrome: { webview: bridge } },
        location,
        AbortController,
        TextDecoder,
        Uint8Array,
        Date: class extends Date {
            constructor(...arguments_) { super(...(arguments_.length ? arguments_ : [now])); }
            static now() { return now; }
        },
        setTimeout: (callback, delay) => { const id = ++nextTimer; timers.set(id, { callback, delay }); return id; },
        clearTimeout: id => timers.delete(id),
        fetch: async (path, options) => {
            requests.push({ path, options });
            const response = responses.shift();
            if (typeof response === "function") {
                return response(path, options);
            }
            if (!response) {
                throw new Error(`Unexpected request: ${path}; token=${accessToken}`);
            }
            return response;
        },
    });
    return {
        context, location, listeners, requests, messages, timers, completed,
        install: () => vm.runInContext(source, context),
        send: (value = request) => { for (const listener of [...listeners]) listener({ data: value }); },
    };
}

async function query(responses, value = request, harnessOptions) {
    const harness = createHarness([jsonResponse({ accessToken }), ...responses], harnessOptions);
    harness.install();
    harness.send(value);
    const result = await harness.completed;
    return { harness, result };
}

test("selects the requested account rather than the first or default account", async () => {
    const { harness, result } = await query([jsonResponse({
        default: "other",
        accounts: {
            other: { account: { account_id: "other-account" }, entitlement: { expires_at: "2098-01-01T00:00:00Z" } },
            selected: { account: { account_id: accountId }, entitlement: { expires_at: futureExpiration } },
        },
    })]);
    assert.deepEqual(result, { kind: "cqtb.subscription", nonce: request.nonce, status: "ok", accountId, expiresAt: futureExpiration });
    assert.equal(harness.requests.length, 2);
    assert.equal(harness.listeners.size, 0);
    assert.equal(harness.timers.size, 0);
    for (const { path, options } of harness.requests) {
        assert.equal(new URL(path).origin, "https://chatgpt.com");
        assert.equal(options.method, "GET");
        assert.equal(options.credentials, "same-origin");
        assert.equal(options.redirect, "error");
        assert.equal(options.cache, "no-store");
    }
    assert.equal(harness.requests[0].options.headers.Authorization, undefined);
    assert.equal(harness.requests[1].options.headers.Authorization, `Bearer ${accessToken}`);
    assert.equal(JSON.stringify(result).includes(accessToken), false);
});

test("a different logged-in account never falls back to its subscription", async () => {
    const { harness, result } = await query([jsonResponse({ accounts: [{ account: { account_id: "other-account" }, entitlement: { expires_at: futureExpiration } }] })]);
    assert.equal(result.status, "account_mismatch");
    assert.equal(result.expiresAt, null);
    assert.equal(harness.requests.length, 2);
    assert.equal(JSON.stringify(result).includes("other-account"), false);
});

test("account identities may be carried by the map key or outer record", async t => {
    const records = [
        { accounts: { [accountId]: { account: { plan_type: "pro" }, entitlement: { expires_at: futureExpiration } } } },
        { accounts: [{ account_id: accountId, account: { plan_type: "pro" }, entitlement: { expires_at: futureExpiration } }] },
        { accounts: [{ id: "record-not-account", chatgpt_account_id: accountId, entitlement: { expires_at: futureExpiration } }] },
    ];
    for (const [index, response] of records.entries()) {
        await t.test(String(index), async () => {
            const { result, harness } = await query([jsonResponse(response)]);
            assert.equal(result.status, "ok");
            assert.equal(result.expiresAt, futureExpiration);
            assert.equal(harness.requests.length, 2);
        });
    }
});

test("default and canonical aliases of the same account do not mean a different login", async () => {
    const record = { account: { account_id: accountId }, entitlement: { expires_at: futureExpiration } };
    const { result, harness } = await query([jsonResponse({ accounts: { default: record, [accountId]: record } })]);
    assert.equal(result.status, "ok");
    assert.equal(result.expiresAt, futureExpiration);
    assert.equal(harness.requests.length, 2);
});

test("different dates for the same account use account-scoped subscription lookup", async () => {
    const { result, harness } = await query([
        jsonResponse({ accounts: {
            default: { account_id: accountId, entitlement: { expires_at: futureExpiration } },
            [accountId]: { account_id: accountId, entitlement: { expires_at: "2026-12-20T10:30:00Z" } },
        } }),
        jsonResponse({ account_id: accountId, active_until: "2026-11-30T10:30:00Z" }),
    ]);
    assert.equal(result.status, "ok");
    assert.equal(result.expiresAt, "2026-11-30T10:30:00.000Z");
    assert.equal(harness.requests.length, 3);
    assert.equal(new URL(harness.requests[2].path).searchParams.get("account_id"), accountId);
});

test("GUID casing is equivalent but opaque identifiers remain case-sensitive", async t => {
    await t.test("GUID", async () => {
        const guid = "AABBCCDD-1122-3344-5566-778899AABBCC";
        const { result } = await query([jsonResponse({ accounts: {
            [guid]: { account_id: guid.toLowerCase(), entitlement: { expires_at: futureExpiration } },
        } })], { ...request, accountId: guid });
        assert.equal(result.status, "ok");
        assert.equal(result.accountId, guid);
    });
    await t.test("opaque", async () => {
        const { result } = await query([jsonResponse({ accounts: [{ account_id: accountId.toUpperCase() }] })]);
        assert.equal(result.status, "account_mismatch");
    });
});

test("unknown structures and conflicting identities are not reported as a wrong login", async t => {
    const guid = "aabbccdd-1122-3344-5566-778899aabbcc";
    const cases = [
        {}, { accounts: [] }, { accounts: { default: { account: { plan_type: "pro" } } } },
        { accounts: [{ account_id: accountId, account: { account_id: "other-account" } }] },
        { accounts: { [accountId]: { account_id: "other-account" } } },
        { accounts: { [guid]: { account_id: accountId } } },
    ];
    for (const [index, response] of cases.entries()) {
        await t.test(String(index), async () => {
            const { result, harness } = await query([jsonResponse(response)]);
            assert.equal(result.status, "account_unverified");
            assert.equal(result.expiresAt, null);
            assert.equal(harness.requests.length, 2);
        });
    }
});

test("conflicting subscription identifiers are rejected without blaming the login", async () => {
    const { result } = await query([
        jsonResponse({ accounts: [{ account_id: accountId }] }),
        jsonResponse({ account_id: accountId, account: { account_id: "other-account" }, active_until: futureExpiration }),
    ]);
    assert.equal(result.status, "account_unverified");
    assert.equal(result.expiresAt, null);
});

test("missing requested account cannot implicitly select the sole account", async () => {
    const harness = createHarness();
    harness.install();
    harness.send({ ...request, accountId: "" });
    assert.equal((await harness.completed).status, "account_unverified");
    assert.equal(harness.requests.length, 0);
});

test("expired entitlement uses subscriptions fallback and URL-encodes the exact account", async () => {
    const unusualAccountId = "account?work&space=one";
    const { harness, result } = await query([
        jsonResponse({ account: { id: unusualAccountId }, entitlement: { expires_at: "2020-01-01T00:00:00Z" } }),
        jsonResponse({ account_id: unusualAccountId, active_until: Date.parse(futureExpiration) / 1000 }),
    ], { ...request, accountId: unusualAccountId });
    assert.equal(result.status, "ok");
    assert.equal(result.expiresAt, futureExpiration);
    assert.equal(harness.requests[2].path, `https://chatgpt.com/backend-api/subscriptions?account_id=${encodeURIComponent(unusualAccountId)}`);
});

test("uses account expiry if entitlement has no valid date", async () => {
    const { result, harness } = await query([jsonResponse({ accounts: [{ account: { workspace_id: accountId, expires_at: Date.parse(futureExpiration) }, entitlement: { expires_at: false } }] })]);
    assert.equal(result.expiresAt, futureExpiration);
    assert.equal(harness.requests.length, 2);
});

test("subscription fallback rejects an explicitly different account", async () => {
    const { result } = await query([
        jsonResponse({ accounts: [{ account_id: accountId }] }),
        jsonResponse({ account_id: "someone-else", active_until: futureExpiration }),
    ]);
    assert.equal(result.status, "account_mismatch");
    assert.equal(result.expiresAt, null);
});

test("subscription fallback supports expires_at and rejects malformed date types", async t => {
    await t.test("expires_at", async () => {
        const { result } = await query([jsonResponse({ accounts: [{ account_id: accountId }] }), jsonResponse({ expires_at: futureExpiration })]);
        assert.equal(result.expiresAt, futureExpiration);
    });
    await t.test("missing dates", async () => {
        const { result } = await query([jsonResponse({ accounts: [{ account_id: accountId }] }), jsonResponse({ active_until: true, expires_at: "123" })]);
        assert.equal(result.status, "not_provided");
        assert.equal(result.expiresAt, null);
    });
});

test("a subscription record ID is not mistaken for an account ID", async () => {
    const { result } = await query([
        jsonResponse({ accounts: [{ account_id: accountId }] }),
        jsonResponse({ id: "subscription-123", active_until: futureExpiration }),
    ]);
    assert.equal(result.status, "ok");
    assert.equal(result.expiresAt, futureExpiration);
});

test("expired and out-of-range subscription dates report not_provided immediately", async t => {
    for (const value of ["2020-01-01T00:00:00Z", new Date(testNow).toISOString(), "2099-10-20T10:30:00Z"] ) {
        await t.test(value, async () => {
            const { result, harness } = await query([
                jsonResponse({ accounts: [{ account_id: accountId }] }),
                jsonResponse({ active_until: value, expires_at: value }),
            ]);
            assert.equal(result.status, "not_provided");
            assert.equal(result.expiresAt, null);
            assert.equal(harness.timers.size, 0);
        });
    }
});

test("over-ten-year entitlement uses subscription fallback instead of returning ok", async () => {
    const { result, harness } = await query([
        jsonResponse({ accounts: [{ account: { account_id: accountId }, entitlement: { expires_at: "2099-10-20T10:30:00Z" } }] }),
        jsonResponse({ active_until: futureExpiration }),
    ]);
    assert.equal(result.status, "ok");
    assert.equal(result.expiresAt, futureExpiration);
    assert.equal(harness.requests.length, 3);
});

test("invalid primary dates fall back to valid alternate date fields", async t => {
    await t.test("account expires_at", async () => {
        const { result, harness } = await query([jsonResponse({
            account: { account_id: accountId, expires_at: futureExpiration },
            entitlement: { expires_at: "2020-01-01T00:00:00Z" },
        })]);
        assert.equal(result.status, "ok");
        assert.equal(result.expiresAt, futureExpiration);
        assert.equal(harness.requests.length, 2);
    });
    await t.test("subscription expires_at", async () => {
        const { result } = await query([
            jsonResponse({ accounts: [{ account_id: accountId }] }),
            jsonResponse({ active_until: "2099-10-20T10:30:00Z", expires_at: futureExpiration }),
        ]);
        assert.equal(result.status, "ok");
        assert.equal(result.expiresAt, futureExpiration);
    });
});

test("ten-year upper bound matches native calendar years including leap-day clamping", async t => {
    const leapNow = Date.parse("2028-02-29T08:00:00.000Z");
    for (const [value, status] of [["2038-02-28T08:00:00.000Z", "ok"], ["2038-02-28T08:00:00.001Z", "not_provided"]]) {
        await t.test(value, async () => {
            const { result } = await query([
                jsonResponse({ accounts: [{ account_id: accountId }] }),
                jsonResponse({ active_until: value }),
            ], request, { now: leapNow });
            assert.equal(result.status, status);
            assert.equal(result.expiresAt, status === "ok" ? value : null);
        });
    }
});

test("HTTP authentication and challenge failures produce sanitized statuses", async t => {
    for (const [statusCode, expected] of [[401, "auth_required"], [403, "verification_required"], [500, "failed"]]) {
        await t.test(String(statusCode), async () => {
            const harness = createHarness([new Response(`<html>${accessToken}</html>`, { status: statusCode, headers: { "content-type": "text/html" } })]);
            harness.install();
            harness.send();
            const result = await harness.completed;
            assert.equal(result.status, expected);
            assert.equal(JSON.stringify(result).includes(accessToken), false);
            assert.deepEqual(Object.keys(result).sort(), ["kind", "nonce", "status", "accountId", "expiresAt"].sort());
        });
    }
});

test("Cloudflare challenge header is recognized independently of status", async () => {
    const harness = createHarness([new Response("challenge", { headers: { "content-type": "text/html", "cf-mitigated": "challenge" } })]);
    harness.install();
    harness.send();
    assert.equal((await harness.completed).status, "verification_required");
});

test("unavailable session and exception details cannot leak through the bridge", async t => {
    await t.test("no session token", async () => {
        const harness = createHarness([jsonResponse({ user: { name: "private-user" } })]);
        harness.install();
        harness.send();
        const result = await harness.completed;
        assert.equal(result.status, "auth_required");
        assert.equal(JSON.stringify(result).includes("private-user"), false);
    });
    await t.test("network exception", async () => {
        const { result } = await query([() => { throw new Error(`Do not reveal ${accessToken}`); }]);
        assert.equal(result.status, "failed");
        assert.equal(JSON.stringify(result).includes(accessToken), false);
    });
});

test("requires JSON media type, structured JSON, and a response below one MiB", async t => {
    const cases = [
        new Response("{}", { headers: { "content-type": "text/html" } }),
        jsonResponse("a string is not an object"),
        jsonResponse({}, { headers: { "content-type": "application/json", "content-length": "1048577" } }),
        new Response(`{"data":"${"x".repeat(1024 * 1024)}"}`, { headers: { "content-type": "application/json" } }),
    ];
    for (const [index, response] of cases.entries()) {
        await t.test(String(index), async () => {
            const harness = createHarness([response]);
            harness.install();
            harness.send();
            assert.equal((await harness.completed).status, "failed");
        });
    }
});

test("a non-official origin installs no listener and performs no requests", () => {
    const harness = createHarness([], { origin: "https://chatgpt.com.attacker.invalid" });
    harness.install();
    harness.send();
    assert.equal(harness.listeners.size, 0);
    assert.equal(harness.requests.length, 0);
});

test("repeated installations replace the listener and each installation is one-shot", async () => {
    const harness = createHarness([
        jsonResponse({ accessToken }),
        jsonResponse({ accounts: [{ account_id: accountId, expires_at: futureExpiration }] }),
    ]);
    harness.install();
    harness.install();
    harness.install();
    assert.equal(harness.listeners.size, 1);
    harness.send({ kind: "unrelated", nonce: request.nonce, accountId });
    assert.equal(harness.listeners.size, 1);
    harness.send();
    harness.send();
    assert.equal((await harness.completed).status, "ok");
    assert.equal(harness.requests.length, 2);
    assert.equal(harness.messages.length, 1);
    assert.equal(harness.listeners.size, 0);
    assert.equal(harness.context.window.__codexQuotaSubscriptionReader, undefined);
});

test("reinstallation cancels an in-flight query without posting a stale result", async () => {
    let rejectFetch;
    const harness = createHarness([(_path, { signal }) => new Promise((_resolve, reject) => {
        rejectFetch = reject;
        signal.addEventListener("abort", () => reject(new Error("aborted")));
    })]);
    harness.install();
    harness.send();
    assert.equal(typeof rejectFetch, "function");
    harness.install();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(harness.requests[0].options.signal.aborted, true);
    assert.equal(harness.messages.length, 0);
    assert.equal(harness.listeners.size, 1);
});

test("twenty-second timeout aborts the query and clears the timer", async () => {
    const harness = createHarness([(_path, { signal }) => new Promise((_resolve, reject) => {
        signal.addEventListener("abort", () => reject(new Error("aborted")));
    })]);
    harness.install();
    harness.send();
    const [{ callback, delay }] = [...harness.timers.values()];
    assert.equal(delay, 20_000);
    callback();
    assert.equal((await harness.completed).status, "failed");
    assert.equal(harness.timers.size, 0);
});
