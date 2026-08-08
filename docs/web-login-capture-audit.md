# Web-login capture audit & CLI-detection hypotheses (2026-07-17)

Context: the Kimi login window never closed after a successful sign-in. Root cause and the
fixes shipped for it are below, followed by the same analysis applied to every other
web-login provider (most are untestable without accounts — those entries are hypotheses
with sources).

## Root cause (Kimi)

The injected capture script read the session token via `document.cookie` (`kimi-auth`) to
build the `Authorization: Bearer` header that Kimi's billing gateway requires (anonymous
calls return `401 {"code":"unauthenticated"}`). `kimi-auth` is HttpOnly, so page JS sees an
empty string, the fetch fails forever, capture never completes, and the (unbounded, visible)
login window never closes.

## Fixes shipped

1. **CLI-first Kimi provider** (`winui/Providers/KimiProvider.cs`). When the Kimi Code CLI
   is installed, its OAuth credentials at `%USERPROFILE%\.kimi-code\credentials\kimi-code.json`
   are used against `GET https://api.kimi.com/coding/v1/usages` — verified live. Access
   tokens last ~15 min; refresh via `POST https://auth.kimi.com/api/oauth/token`
   (client_id `17e5f671-d194-4dfb-9706-5516cb48c098`, extracted from the CLI bundle).
   Refresh tokens rotate, so refreshed credentials are written back atomically under the
   CLI's own `oauth/kimi-code.lock`. No embedded browser needed at all in this mode.
   (The legacy official `kimi-cli` stored the same shape at `~/.kimi/credentials/<provider>.json`
   and used the same usage endpoint — not implemented, `kimi migrate` exists.)
2. **Native cookie capture** (`WebLoginService.NativeCookieCaptures`): the host reads the
   auth cookie through `CoreWebView2.CookieManager` (which *can* see HttpOnly cookies) and
   calls the usage API natively. Wired for `kimi` (verified header requirement) and `manus`
   (hypothesis — same pattern, `session_id`/`__Secure-session_id` cookie, per CodexBar's
   ManusUsageFetcher).
3. **Response sniffing** (`WebLoginService.IsNativeCapturedResponse`): when the logged-in
   dashboard itself calls the usage API the parser understands, the response body is
   captured natively — previously alibaba-only, now also `kimi`, `manus`, and `windsurf`
   (windsurf is a hypothesis: only works if their ConnectRPC web client uses the JSON codec).
   A body that fails the provider's parser is ignored, so extra matches are harmless.

## Audit of all web-login init scripts (2026-07-17)

Risk = "the visible login window may never close after a successful login".

| Provider | Auth mechanism of injected fetch | Risk | Notes |
|---|---|---|---|
| kimi | `document.cookie` → Bearer (required) | **HIGH — fixed** | See above |
| manus | `document.cookie` (`__Secure-session_id`) → Bearer | **HIGH — mitigated** | Cookie capture + sniffing added (untested) |
| windsurf | localStorage `devin_*` tokens → custom headers; waits forever if keys missing | **MED — mitigated** | Sniffing of `GetPlanStatus` added (untested); tokens may live only in browser localStorage, not the WebView |
| amp | HTML scrape of `freeTierUsage` in `outerHTML` | MED | Breaks if usage is client-rendered; better path: local CLI creds (below) |
| ollama | HTML scrape of settings DOM | MED | Brittle to markup changes; no API to sniff |
| opencode / opencodego | regex-extracts `wrk_…` id from server-fn text | MED | Fragile parsing; local `auth.json` exists but holds downstream-provider keys, not opencode.ai session |
| bayesdl, mimo | relative fetch, cookies auto-sent | LOW | Composite two-endpoint payloads → sniffing not applicable |
| cursor, augment, factory, commandcode | `credentials:'include'`, no cookie read | LOW | Parsers expect composite wrapper shapes → sniffing would need per-endpoint adapters |
| minimax, perplexity, t3chat, abacus, stepfun, mistral | `credentials:'include'`, no cookie read | LOW | Parsers accept raw responses; sniffing possible later if reports come in |
| alibaba / alibabatokenplan | sec_token page-readable + HTML fallback | LOWEST | Already has sniffing + NavigationCompleted close |

## CLI-detection hypotheses for other providers (not implemented)

Candidates for the same "read local CLI credentials → call usage endpoint" pattern as
Claude/Gemini/Kimi. Windows paths marked (e) are extrapolated from XDG conventions; verify
on a real install before shipping. Primary source: CodexBar docs
(github.com/steipete/CodexBar) and tokscale (github.com/junhoyeo/tokscale).

- **Amp — best next candidate.** Token at `%USERPROFILE%\.local\share\amp\secrets.json`,
  key literally named `apiKey@https://ampcode.com/` (long-lived, no refresh). Usage:
  `POST https://ampcode.com/api/internal` body `{"method":"userDisplayBalanceInfo","params":{}}`
  → `result.display_text` (human-readable balance string; needs text parsing).
- **Factory (droid).** Endpoints verified (`GET api.factory.ai/api/billing/limits`,
  `GET app.factory.ai/api/organization/subscription/usage?useCache=true`, Bearer), but the
  subscription OAuth token lives in **Windows Credential Manager**, not a file. Only a
  manually created `fk-` API key at `%USERPROFILE%\.factory\.env` / `config.json` is
  disk-readable — partial detection at best.
- **Windsurf.** `%APPDATA%\Windsurf\User\globalStorage\state.vscdb` (e) contains
  `windsurf.settings.cachedPlanInfo` in `ItemTable` — the quota itself, readable offline
  but stale (refreshes when the editor runs). Requires a SQLite reader dependency.
- **Cursor.** App bearer in `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (e)
  `ItemTable`; usage `GET https://cursor.com/api/usage-summary` — CodexBar's fallback path.
  Also SQLite.
- **OpenCode.** `%USERPROFILE%\.local\share\opencode\auth.json` (verified in source) holds
  *downstream provider* keys — no first-party quota endpoint; not useful for opencode.ai quota.
- **Qwen-code / iFlow.** OAuth creds at `~/.qwen/oauth_creds.json` (client_id
  `f0304373b74a44d2b584a3fb70ca9e56`, refresh `POST chat.qwen.ai/api/v1/oauth2/token`) and
  `~/.iflow/oauth_creds.json` (refresh `POST iflow.cn/oauth/token`) — but neither has a
  first-party quota endpoint (and both programs are being wound down in 2026).
- **StepFun / legacy Moonshot.** No dedicated CLI creds file; static API keys work against
  `GET api.stepfun.ai/v1/accounts` and `GET api.moonshot.ai/v1/users/me/balance` — these fit
  the existing SimpleApiProvider pattern better than CLI detection.
- **Mistral, Perplexity, Abacus.** Web-session only; no local credential worth reading.
