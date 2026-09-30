# osu!lazer 8k (branch 8k-next)

This is a patched osu!lazer, not upstream work. Before anything else read `8k/NEXT.md` (current state,
rules and agreed next steps), then `8k/README.md` and `8k/PLAN-lows.md`.

Hard rules, repeated from `8k/NEXT.md`:
- Plan in `8k/PLAN-lows.md` before code. Ask before starting anything not listed under "Agreed next steps".
- The framework patches are on branch `8k-next-framework` of this same repo; check it out next to this
  one (`git worktree add ../osu-framework 8k-next-framework`) or the build fails.
- Push only to `8k-next` and `8k-next-framework`. Never force-push, never touch `master`.
- This repo is public: no beatmap names, usernames, scores, user paths or screenshots anywhere.
- Never trade input delay for fps. New behaviour is opt-in behind an env var; defaults change only on the owner's yes.
