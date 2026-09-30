# Review #49: Outbound SSRF Closure

## Objective and Scope

Finish the in-progress SSRF remediation for untrusted image, link and page URLs.
Do not change pipeline, persistence, platform adapters, UI or README files.
Keep IHttpFetcher and IImageDownloader signatures, configured model endpoints,
image cache, refreshed-image retry and the private-host test override intact.

## Initial Architecture and Impact

- HttpFetcher owns sockets. Secure instances disable proxies and use a DNS
  connection callback that filters private addresses and connects to the selected
  IP literal, not a hostname resolved a second time.
- SafeUrl performs HTTP(S)/host/literal checks and manually validates redirects.
- LinkPreviewer and WebSearchService page reads already use the redirect helper.
- ImageDownloader still duplicates host checks and uses GetAsync. With automatic
  redirects disabled it cannot complete a normal public redirect chain.
- ModelTransport calls DownloadAsDataUrl; OpenAiClient delegates sticker downloads
  to DownloadBytesAsync. Both require unchanged nullable results and cancellation.
- BuildModelAndMediaLayer separates trusted model/search-model clients from
  untrusted image/link/search-page clients. Other configured services stay trusted.
- Services cannot explicitly import Adapters (R9). IHttpFetcher is globally
  imported; the shared address policy stays in Services/Net.

## Contracts and Data Flow

Untrusted URL -> SafeUrl.TryValidate -> SendFollowingRedirectsAsync -> secure
HttpFetcher -> DNS addresses -> OutboundAddressPolicy -> socket to accepted IP.
Each redirect is validated before another request; intermediate responses are
disposed. Image redirects share the original client response-header timeout
budget across all hops; body reads retain the original caller cancellation token.
QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1 permits private hosts but never
non-HTTP(S) input, including redirect locations. No public interfaces are added.

## Milestones

1. Add an independent SsrfProbe referencing the production assembly and invoking
   its internal image downloader/address policy without visibility changes.
   First run the public-image-redirect regression before changing ImageDownloader.
2. Replace ImageDownloader's duplicate URL policy and GetAsync with SafeUrl's
   existing validation/redirect helper. Preserve caps, cache, retry and cancellation.
3. Review HttpFetcher candidate filtering/literal pinning, remove stale imports,
   correct new-block indentation and misplaced comments in the scoped network code.
4. Extend the probe for literal/private/mapped/ULA addresses, loopback DNS, mixed
   candidate filtering/pinning concepts, private/public redirects, bounded loops,
   image refresh/cache/size limits and production composition boundaries.

## Risks and Limits

- DNS rebinding is blocked at connection time; syntax checks alone are insufficient.
- Mixed DNS candidates may include both private and public addresses: only public
  candidates may be connected. Probe that classification synthetically and inspect
  the production callback's literal socket target. Do not connect to public hosts.
- Constructor override and consumers must agree on private-host permission.
- A test fetcher does not auto-redirect; caller tests prove per-hop handling.
- Composition checks inspect only the network wiring source, never invoke Build
  (which would load persisted application state and start services).
- Link/source requests retain their own headers; no model authorization goes to
  arbitrary page targets. Trusted model and internal configured clients remain
  separate and are not globally hardened.

## Verification

```powershell
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.SsrfProbe/BotAgent.SsrfProbe.csproj -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
git diff --check
```

The probe uses synthetic responses, disposable test data, and localhost DNS only.
No production state, external HTTP/DNS targets or model calls are exercised.

## Execution Evidence

- Red baseline: the independent probe reported `image=False; requests=1` for a
  public image redirect when ImageDownloader called GetAsync directly.
- Green: `dotnet run --project tests/BotAgent.SsrfProbe/BotAgent.SsrfProbe.csproj
   -c Release` passed 77 checks with 0 failures, including a 50 ms chain budget
  with 30 ms synthetic delay per hop (cancelled within two sends). It used synthetic responses only;
  the secure HttpFetcher checks used OS localhost DNS and port 9, with no public
  network request.
- Main build and `--no-restore -t:Rebuild` passed with 0 errors. The shared branch currently carries five
  pre-existing warnings outside this SSRF change.
- Architecture probe: final run passed 92 checks with 0 failures, including R9
  (Services cannot reference Adapters). An earlier parallel Feishu edit exceeded
  the method-length ratchet; its owner corrected it before the final run.
- Integration harness project build passed. No integration scenario was started
  in this task.
- `git diff --check` passed.
