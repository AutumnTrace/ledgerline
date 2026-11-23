# Error codes

Every error response is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem document
(`application/problem+json`). Business errors carry a stable `code` and a `type` URI pointing at the
matching section below, plus the request's `traceId`. Branch on `code`, not on `title` or `detail`.

```json
{
  "type": "https://github.com/AutumnTrace/ledgerline/blob/main/docs/errors.md#insufficient-funds",
  "title": "Insufficient funds.",
  "status": 422,
  "detail": "Account player:p-1001:cash:EUR has 75.00 EUR available, 9999.99 EUR was requested.",
  "code": "insufficient-funds",
  "accountCode": "player:p-1001:cash:EUR",
  "availableMinor": 7500,
  "requestedMinor": 999999,
  "traceId": "00-…"
}
```

| Code | Status | Retry with the same transaction id? |
|---|---|---|
| [`validation-failed`](#validation-failed) | 400 | No, fix the request |
| [`malformed-request`](#malformed-request) | 400 | No, fix the request |
| [`currency-mismatch`](#currency-mismatch) | 400 | No |
| [`not-found`](#not-found) | 404 | Only once the referenced resource exists |
| [`idempotency-key-reused`](#idempotency-key-reused) | 409 | No, use a new transaction id |
| [`invalid-state`](#invalid-state) | 409 | No |
| [`write-conflict`](#write-conflict) | 409 | **Yes**, after `Retry-After` |
| [`insufficient-funds`](#insufficient-funds) | 422 | Yes, once funds arrive |
| [`unbalanced-entry`](#unbalanced-entry) | 422 | No (indicates a bug) |

## validation-failed

The request can never be valid: a missing field, an identifier with characters outside
`[A-Za-z0-9._-]`, a non-positive amount, an unknown or lower-case currency, a bad statement `cursor` or
`limit`. `errors` lists the offending fields. Requests rejected by the built-in validation use the
standard ASP.NET Core validation problem shape (same `errors` dictionary, no `code`).

## malformed-request

The body is not valid JSON or does not match the expected shape.

## currency-mismatch

An amount in one currency was applied to an account in another. Wallets are per currency, so this
normally means the request named the wrong currency.

## not-found

The referenced bet, withdrawal, account, entry or round does not exist. A rollback that names another
player's bet also gets `not-found`, so transaction ids cannot be probed across players.

## idempotency-key-reused

The transaction id was already used for a **different** request (different amount, player, round, or a
different operation: bets, wins and rollbacks share one id space). Repeating the *same* request is not
an error; it returns `200` with the original result and `Idempotent-Replayed: true`.

## invalid-state

The operation is valid in general but not now: rolling back a bet twice, rolling back a win, completing
a reversed withdrawal or reversing a completed one.

## write-conflict

The command lost an optimistic-concurrency race on every attempt (default: 1 try + 5 retries with
jittered backoff). Nothing was posted. Retry with the **same** transaction id after the `Retry-After`
delay; idempotency makes that safe even if an earlier attempt had in fact committed.

## insufficient-funds

A player wallet would go below zero. `accountCode`, `availableMinor` and `requestedMinor` say by how much.
Rejected commands are not recorded, so the same transaction id can be retried once funds arrive.

## unbalanced-entry

The postings of an entry do not sum to zero per currency. Commands always build balanced entries, so
this signals a programming error and should never be seen through the API.
