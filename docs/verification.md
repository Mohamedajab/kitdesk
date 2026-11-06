# Verification

6 November 2026, Windows 11, .NET SDK 10.0.403 / .NET 10, Node 24.16.0.

## Backend

Local `dotnet test --logger 'trx;LogFileName=backend.trx' --results-directory TestResults` passed **22 tests, zero skips**, against a dedicated PostgreSQL 17.11 database. The local server uses the Zonky Maven Central package of PostgreSQL Windows binaries; this is a genuine PostgreSQL process, not an in-memory substitute. Docker Desktop is installed but its engine was not reachable locally.

`dotnet build -c Release -warnaserror` passed with zero warnings/errors. `dotnet format` was applied; CI separately verifies formatting without rewriting files.

Covered scenarios:

- EF migrations applied to a fresh, isolated schema; generated OpenAPI is reachable.
- Eight competing HTTP checkout requests: one 201, seven 409; one active database loan remains.
- Repeat returns preserve the original return record, and the item can be borrowed again.
- Anonymous requests cannot read records. Staff cannot add equipment; managers can.
- Missing antiforgery tokens fail both before and after authentication.
- Independent visitors cannot read or mutate each other's equipment/loans; the database rejects a cross-workspace foreign key.
- Expiry blocks access and later demo creation removes expired accounts and records.
- Due-date validation, empty borrower labels and overdue/returned distinction.
- Concurrent equipment creation respects the 50-item cap; loan history counts towards the 100-loan cap.
- The shared 200-workspace cap blocks admission until expired workspaces make room.
- Rate-limit rejection includes Problem Details and Retry-After. Most backend tests raise the admission allowance to exercise business rules; the rate-limit test explicitly sets it to one.
- An independent app host reads the stored Data Protection key and authenticates an existing session cookie.

The app's normal public allowance remains five demo starts per minute per peer and 60 mutations per minute per workspace. No capacity or multi-region performance claim is established.

## Frontend and deployment

Frontend lint and TypeScript/production build pass. The dependency audit reported zero vulnerabilities after using the same small Oxlint setup as the existing portfolio apps.

Local `npm run test:browser` passed all three Chromium cases: staff/manager workflow, repeated borrowing after return, native validation, keyboard focus/escape, axe checks, widths 390/768/1440, a conflicting edit with preserved input, separate visitor workspaces and persistence across reload.

The first browser run caught an absolutely positioned screen-reader label extending outside the mobile viewport. Positioning its containing scroll region fixed the cause; the rerun passed without hiding page overflow.

The first CI run passed restore, formatting, the warning-free Release build and all 21 then-current PostgreSQL tests, but the first browser request raced application startup. CI now waits for `/healthz` before browser checks. No application failure was hidden with a test retry.

[CI run 42209651549](https://github.com/Mohamedajab/kitdesk/actions/runs/42209651549), head `3fbe25d4ee715c4e8c4d785d814ad23c3944fe00`, passed all 22 database tests with zero skips, formatting, the warning-free Release build, clean locked restores, frontend lint/type checking/build, all three Chromium cases and the multi-stage Docker image build on Ubuntu. PR #1 was merged after this run passed. The same runtime is being deployed on Render Free.

## Evaluation boundaries

No real borrowers, production onboarding, payments, email or external user data are involved. Public sessions expire after an hour, but physical cleanup is opportunistic. A user with database access can read Data Protection keys; secure database access is essential. Public demo role switching is intentionally permitted only within its own workspace.
