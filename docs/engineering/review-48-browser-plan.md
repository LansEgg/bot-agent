# Issue 48 Synthetic Browser Acceptance

## Scope And Architecture

Only session-related functions in `app.js`, FrontendProbe and this independent fixture are in scope. Existing session HTML escaping was already present. Before this correction, the execution-record handler interpolated decoded dataset IDs into a CSS selector. The correction uses a static selector followed by exact dataset equality; IDs remain unchanged. No production panel, APIs, private data, deployment or CI changes are involved.

## Contracts And Milestones

1. Extract actual unchanged `escapeHtml`, `api`, session renderer and import bindings from `app.js`; fail if boundaries are absent or ambiguous.
2. Run in an isolated browser page with native DOM and fake-fetch responses, preserving exact chat/session/pi IDs in action payloads.
3. Reproduce hostile-ID expansion failure first, then minimally replace selector interpolation with exact dataset comparison.
4. Add a focused non-browser regression and rerun FrontendProbe and browser acceptance.

## Edges And Risks

Cover quotes, angle brackets, ampersands, brackets, backslashes and selector metacharacters. Assert literal titles/prompt/result/error text and attribute roundtrip, no injected nodes/events/scripts, usable select/delete/import/expansion. The extractor is a test-only boundary contract, not a copied renderer. A malformed ID must never select a different session.

## Verification

`node tests/BotAgent.FrontendProbe/probe.mjs`

`node tests/BotAgent.FrontendProbe/browser-fixture.mjs` prints an ephemeral loopback URL. Open only that URL in a new isolated browser context; inspect `window.acceptanceResult` when finished. The server provides only fixture HTML, test glue and extracted source. All API calls use page fake fetch. Stop the managed server job after acceptance.

## Observed Red And Green

Before the lookup correction, the real Chrome 154 isolated page returned **38 passed / 5 failed**. Four expansion-open assertions failed; the fifth was browser error detection. Quote IDs produced native `querySelector` SyntaxError; backslash/selector-metacharacter IDs did not open their exact session. The XSS execution marker stayed at zero. This was observed using the actual current source, without changing or copying its renderer.

The test-first command `node tests/BotAgent.FrontendProbe/probe.mjs` then returned **287 passed / 3 failed**, exit 1, for the newly added hostile-ID lookup regression. After the minimal correction the same command returned **296 passed / 0 failed**, exit 0. The logged synthetic status-interface error is an existing intentional fail-closed test, not a failing assertion.

Reloading the isolated native-browser fixture after the correction returned **43 passed / 0 failed**. Coverage was then extended to select/delete every hostile ID and import two pi sessions, including a compact complete SVG event payload that fits the 40-character title truncation. Final acceptance returned **50 passed / 0 failed** in two independent isolated contexts, `errors=[]`, `marker=0`, and 26 fake-fetch calls per run. Native DOM checks include exact attribute roundtrip, no attacker nodes anywhere in the body and exactly the three trusted fixture scripts.

Actual browser operations: start the fixture command above; Chrome tools `new_page` with `isolatedContext`, `navigate_page` to the printed fixture URL, then `evaluate_script: () => window.acceptanceResult`. Before correction the result was red; after correction `navigate_page` reload with `ignoreCache: true` returned green. The network panel showed only three HTTP requests: fixture HTML, acceptance script, and extracted source script. No API requests reached the network.

For a CLI reproduction with the installed browser skill:

```powershell
node tests/BotAgent.FrontendProbe/browser-fixture.mjs
# Use the printed ephemeral URL in another terminal.
playwright-cli -s=review48 open --headed <printed-fixture-url>
playwright-cli -s=review48 eval "JSON.stringify(window.acceptanceResult)"
playwright-cli -s=review48 close
```

The CLI commands are reproduction instructions; this run used Chrome MCP isolated contexts. `node --check` passed for `app.js`, the probe and both new fixture scripts; scoped `git diff --check` passed. Temporary fixture server and browser pages are stopped/closed after verification. No commit, push, production build or deployment was performed; README, CI and unrelated shared-worktree changes were not edited.
