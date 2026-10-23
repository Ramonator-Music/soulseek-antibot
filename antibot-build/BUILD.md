# soulseek-antibot

> "This is a modified version of slskd.  It is not maintained by, endorsed by, or affiliated with the slskd project or its author(s)."

Modified build of [slskd](https://github.com/slskd/slskd) (AGPL-3.0-only, with Additional Terms — see `LICENSE`). Copyright of the modification (c) 2026 Ramonator.

## What it changes / Qué cambia

Incoming upload requests pass through a gate before slskd accepts them:

- **Per-file cap / Tope por archivo:** a user who already downloaded the same file successfully N times (default 2) is rejected with `Already downloaded this file N times`. Failed transfers don't count.
- **Human verification / Verificación humana:** unverified users are rejected with `Verification required: check private messages` and receive a private message with a word. Answering with that word verifies them permanently; then they retry the download.

Everything else is upstream slskd. The full change is `antibot.patch` (against the upstream tag of the same version).

## Configuration (`slskd.yml`)

```yaml
antibot:
  enabled: true
  max_downloads_per_file: 2
  exempt: []                # usernames that bypass both checks
  verification:
    enabled: true
    cooldown_minutes: 10    # min. minutes between challenge messages to the same user
    source_url: <url of this repository>
    # challenge_message / success_message: optional overrides; {word} (shown in capitals) and {source} are replaced
    # each line of a message is sent as a separate private message: the server drops messages with line breaks
```

Verified users are stored in `<app dir>/data/antibot_verified.json`; the HMAC key that derives each user's word in `<app dir>/data/antibot.key`.

A reply counts as correct if, reduced to letters and digits (no accents, no spaces, case-insensitive), it contains the user's word and is at most 40 characters long. This tolerates stray characters some clients append, and keeps a bot from passing by replying with the whole word list.

To verify someone by hand, add their username to `antibot_verified.json` (a JSON array of strings). The file is re-read within a few seconds; no restart needed. Inside the container, for example:

```sh
docker exec slskd sh -c 'f=/app/config/data/antibot_verified.json; jq --arg u "USERNAME" ". + [\$u] | unique" "$f" > "$f.tmp" && mv "$f.tmp" "$f"'
```

A manually verified user is still subject to the per-file cap; to skip both checks, use `exempt` in `slskd.yml`.

## Build / Compilar

```sh
SOURCE_URL=<url of this repository> sh build.sh 0.26.1 1
```

Downloads the upstream source tag, applies `antibot.patch`, takes the prebuilt web UI from the official image of the same version (adding a "modified version" notice), compiles the backend in a resource-limited container and packages it on top of the official image as `rito/soulseek-antibot:0.26.1-antibot.1`.

To update to a new slskd version: `sh build.sh <new version> 1`. If the patch no longer applies, it has to be adjusted by hand.

## Rollback / Volver atrás

Point the service back to `slskd/slskd:<same version>`. The modification adds no database migrations; the two files in `data/` are simply ignored.
