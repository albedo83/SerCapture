> **Avant toute chose : lire `DECISIONS.md`** — il fige le périmètre, l'architecture
> et les choix déjà tranchés avec l'utilisateur. Ne jamais contredire une décision
> qui y figure sans la flaguer d'abord.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

Rappel propre à ce projet : **on gagne l'abstraction par l'usage, pas à l'aveugle.**
On construit la tranche dont le client courant a besoin, derrière une interface propre ;
on ne bâtit pas la HAL complète spéculativement (cf. `DECISIONS.md` §2).

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:

1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]

## 5. Maintain a file called MEMORY.md.

After any significant decision, about direction, format, content, approach, or
strategy, add an entry:

 [Date], [Decision]
What was decided: [the choice made]
Why: [the reasoning]
What was rejected: [alternatives considered and why they were ruled out]

Read MEMORY.md at the start of every session before doing anything. Never contradict
a logged decision without flagging it first.

## 6. Maintain a file called ERRORS.md.

When an approach takes more than 2 attempts to work, log it:

[Task type or description]
What didn't work: [approaches that failed and why]
What worked: [the approach that finally succeeded]
Note for next time: [anything worth remembering for similar tasks]

Check ERRORS.md before suggesting approaches to tasks similar to logged ones. If a
task matches a logged failure, say so and skip to what worked.

## 7. Contexte matériel

Cible : mini-PC **Mele** (Windows, fanless, CPU faible, pas de GPU). Garder le code
de contrôle léger et réactif ; ne pas supposer la performance — la **mesurer** sur la
cible quand c'est un risque (cf. `DECISIONS.md` §5, perf Mele).
