# Writing to the Conduit documents

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before adding to, editing, or reorganising any `conduit-*.md` file.**
>
> **This file owns how these DOCUMENTS are written, and nothing else goes in it.** How CODE is written is
> `conduit-code-style.md` - the two are easy to conflate because both are style, and a rule about
> comments or naming in source belongs there even though you were reading this when you thought of it.
> Open work about the documents themselves goes in `conduit-todo.md` like any other open work.

## Writing to these documents

> **Record reusable Conduit knowledge in the matching subject file, not in agent/session memory.** These
> docs are redistributed with Conduit; per-workspace agent memory is not, so a bridge lesson saved there is
> invisible to every other project and agent and just gets re-derived next session. When you learn something
> durable about the bridge - a verb's behaviour or limits, a gotcha, a workflow rule, a bug and its fix - add
> or update the rule in the relevant subject file instead of writing a memory.

`conduit-instructions.md` is injected into every agent's system prompt, in every project, forever - so a
line added there is paid for by every session whether or not it is ever relevant. A subject file costs
nothing until an agent working on that subject opens it. That asymmetry is the whole design: keep the
index a router, put the knowledge in subject files.

**Installed skills regenerate THEMSELVES after a subject file is edited - do not check that they did.**
The installed skills are generated copies of these documents, and the auto-sync (`ConduitSkillsAutoSync`)
refreshes every already-installed client's copies on the next domain reload, on import of a Conduit
document, or from its filesystem watcher after a two-second debounce, logging one `[Conduit] Skills
auto-synced` line per client it refreshed. It fires deterministically on every trigger, so reading that
line or opening the installed copies after an edit spends tokens confirming what always happens; look only
when a skill is visibly stale. The window's `Connect` / `Update skills` buttons remain the manual
fallback and the ONLY way to install a NEW client - the sync never creates a skills folder that was not
already there.

Write accordingly:

- **This index costs the most - keep subject KNOWLEDGE out of it, but never trim its ROUTING.** A rule
  belongs here only if it applies to essentially every edit; anything narrower goes in a subject file,
  however important it feels. The per-subject descriptions are the exception and are not subject
  knowledge - they are the index doing its one job, and a router too thin to route is worse value than
  the tokens it saved. This was learned the hard way: the descriptions were once trimmed to a terse
  trigger table on the grounds that the skills carried the full text, and a console-noise rule was then
  filed against the play-mode input document. **A skill cannot route you to a skill you have not already
  identified** - skills fire by matching their own description - so the index is the ONLY place a
  "which file owns this?" question can be answered.
- **The subject block in the index is GENERATED - never hand-edit it.** `ConduitSkills.Subjects` is
  the single registration point for a subject document: it produces the skill frontmatter, the sibling
  table inside every skill, and the block between the `CONDUIT-SUBJECTS` markers. Adding a document
  means creating the `.md` and adding one array entry; anything else you "keep in sync" is a copy that
  will drift. One already did - a subject file shipped with a router row and no skill entry, so that
  skill never fired for anyone. **Retiring one is the same single edit** - the next sync deletes the
  installed copies from every client, matched against the record it keeps beside them, and names them in
  its console line. Hand-cleaning a client's skills folder is never needed and risks the user's own
  skills, which live in that same folder.
- **Lead with the rule, in the imperative.** The first clause of a bullet must be the instruction. If a
  reader has to reach the third line to learn what to do, rewrite it.
- **A bullet is 1-4 lines.** Longer means it is either two rules, or it needs its own `###` heading with
  prose beneath. It does not mean the bullet gets longer.
- **Cut session narration: attempt counts, time or quota cost, which session hit it, and a date on a rule
  that cannot expire.** "Cost two rounds of confusion", "burned a session", "three compile cycles were
  spent", "Verified 2026-08-10" - delete those on sight, including from entries written before this rule.
- **Never cut an observed value, a magnitude, or a failed approach.** Those ARE the discriminator, and
  stripping them leaves a rule nobody can check - folklore with a warning label on it. "113 fields on every keystroke"
  and "positional probing locks the editor for minutes" stay, past tense and all - a reader can still try
  the approach that failed. Correct a rule by re-measuring it, never by softening the wording.
- **A superlative in a SCOPE claim is a defect - it absorbs the boundary that made the claim checkable.**
  "every available command", "all of them", "the authoritative list": each reads as emphasis and lands as
  a promise the sentence cannot keep, so a reader applies the rule outside the scope its author meant.
  State what the thing covers AND what it does not, in the same breath. Specimen: a discovery rule said
  "the authoritative list of every available bridge command" and then "the command map for the session" -
  two different scopes in one bullet, and the wider reading sent its own author to call a tool that list
  never covered.
- **When you misread your OWN rule, the rule is the defect - fix the wording, never your memory of it.**
  Your own misreading is the only test of these documents you get for free, because every other reader's
  confusion is invisible to you. So treat it as a confirmed fault and ask which distinction the wording
  dropped, rather than filing it as a lapse of attention and moving on.
- **Never write this file's own history - no "an earlier version of this rule said X".** A reader only
  ever has the current text, so a superseded revision is a belief they cannot hold and a claim they cannot
  check, and it costs the lines the replacement fact needed. Keep the finding, delete the provenance:
  "Udon arithmetic wraps" does the whole job. The same goes for a part Conduit no longer has.
- **The stop sign belongs to the numbered hard stops in `conduit-instructions.md` and appears nowhere
  else.** A subject file states its rules in plain bold. The symbol only carries meaning while it stays
  rare: these documents reached 327 of them against 11 real hard stops, at which point it marked nothing
  and camouflaged the genuine ones. A rule that keeps being ignored earns promotion to that list, never a
  decoration where it already sits.
- **Shouting caps need a rule that was actually broken, not a fact you find important.** "The user
  re-affirmed this" and "an agent did it anyway" earn them; "this is a useful API detail" does not. Where
  they are earned, preserve them verbatim when rewriting - a marker that was paid for outranks your
  judgement about whether it reads shouty. Everywhere else, write the sentence normally.
- **Read a marker as a claim that something was broken, and look for the evidence beside it.** That test
  applies to this file: its caps sit next to the failure that earned them - a router trimmed until a rule
  was misfiled, a generated block kept in sync by hand until it drifted, a proper-noun sweep that cut the
  one name a reader needed - and the phrasings it forbids appear here as quoted specimens, not as uses. A
  marker with no evidence in reach is a defect to fix where it sits, never a reason to discount the rule.
- **Never promote a source-level OBSERVATION into a supported CONTRACT, and say which authority a fact
  came from when the two can disagree.** A decompiled implementation tells you what this build does; the
  vendor's documentation tells you what they promise to keep doing, and a beta is exactly where those
  diverge. So a capability the shipped code happens to allow is written as that and no more - "the
  installed source reaches it, but that is not a supported contract, so recreate it rather than relying
  on it" - because a reader who builds on the observation has no warning when the next release closes it.
  Where a fact is version-pinned to a binary rather than a release, cite the HASH: it is the strongest
  authority available and it lets a reader confirm they hold the same file.
- **NO DATES. Cite the VERSION of whatever governs the fact - the SDK, or a third-party package - and
  otherwise just the source.** Everything written into these documents was verified before it went in;
  that is the entry bar, so a date asserts what the file's existence already guarantees. It is also not a
  substitute for a version: "true on 2026-09-14" cannot tell a reader whether their own install differs,
  while `(SDK 3.10.5)` answers it outright, and where nothing was versioned the date buys false comfort
  rather than a check anybody can run. Name the source so it can be re-read - `(Client log)`,
  `(VRChat creator docs)` - and stop there.
- **A line number ROTS with its version, so never let it carry the fact alone - name the SYMBOL a reader
  can search for, and give the re-deriving command where a whole section is a snapshot of numbers.**
  Observed after an SDK update: the fact was NOT at the line these documents gave, and grepping the
  package for the symbol was the only way back to it. A stale line does not dangle, it misaddresses - it
  lands on a different and plausible line, which reads as authoritative and is worse than absence.
- **One example, and only when the syntax cannot be guessed.** A payload shape or an exact command string
  earns an example. A rule that reads fine in a sentence does not get one.
- **Edit the existing rule, never append a near-duplicate.** Search the subject file first. Several bullets
  restating one procedure is the failure mode that made this split necessary.
- **Open work goes in `conduit-todo.md`, not in a subject file.** A subject file states what is true and
  what to do; anything unfinished - an open bug in the bridge, an undiagnosed condition, an unanswered
  question, a wanted verb - belongs in the backlog. Where an open condition also needs a warning in
  context, leave a two-line note in the subject file and keep the investigation in the backlog.
- **Delete on resolution.** When a bug is fixed, the investigation notes go with it - keep the resulting
  rule only. **A completed TODO is deleted, not annotated as done**; a list that reads "Nothing
  outstanding" followed by finished items is a trophy shelf that costs every future session to scroll past.
- **`(fixed <date>)` is the shape that survives every cleanup - cut it wherever it appears.** It escapes a
  date sweep by reading as provenance about a repair rather than as a date on a rule, and it escapes a
  resolution sweep by sitting inside a rule that is still true. It is forbidden twice over and specimens
  of it were still being found long after both rules were written. The rule stands on its own; when the
  repair happened is not something a reader can act on.
- **Never restate a rule that lives in another file.** Reference it: "see `conduit-compile-console.md`".
- **And never restate a HARD STOP in a subject file - the index is in every session's system prompt
  already.** A stop is the one thing guaranteed to be in front of a reader, so a second copy in the file
  it links to buys nothing and drifts: one was found where the index's version carried two details the
  subject file's had lost, so the file the stop points AT was the weaker copy of it. A subject file adds
  only what the stop omits, and says that is what it is doing.
- **Before describing behaviour, ask whether an AUTHORITATIVE copy already exists - and if it does, cut
  yours and point at it.** What a verb accepts is published by the catalogue, generated from the verb's
  own signature, so it cannot drift; how a class works belongs in that class's summary, where anyone
  about to change it already is. A second copy is paid for by every session that loads the file and goes
  stale the first time the code moves without it. One file had accumulated verb constraint lists, payload
  shapes and a class's private field chain, and its own router row advertised them, so the description was
  inviting the bloat.
- **But where no other copy exists, this IS the home, even when the line reads like description.** The
  test is the existence of an authoritative source, never the grammar of the sentence - a diagnostic
  payload that no catalogue publishes and no reader will go to a transport class to find has nowhere else
  to live, and deleting it for reading like documentation loses it outright. Cutting a duplicate makes the
  document cheaper; cutting an only copy makes it wrong.
- **Give every subject file a preamble naming what does NOT go in it, and which sibling owns that
  instead.** Stating only what a file covers leaves every boundary to the judgement of whoever is holding
  a fact they want to put down, and that judgement is made while their attention is on the fact rather
  than on the filing. The one file carrying such a preamble - it names three siblings and adds "when in
  doubt the answer is NOT this file" - audited clean, while the file with no boundary statement had
  accumulated verb documentation, class internals, an undiagnosed condition and another file's rules. A
  boundary a writer has to read past is one they notice.
- **File a rule by the QUESTION its reader will be asking, not by the noun inside the rule** - and move
  its router row with it. Filing "which object must a UnityEvent point at" under proxy semantics because
  the word proxy appears in it puts it where somebody wiring a button never looks, and a rule in the right
  file under the wrong description is as unfindable as one in the wrong file.
- **Audit the file you are editing against these rules before you add to it.** Every one of the above was
  broken in a single subject file that also carried, in its own preamble, the rule that open work belongs
  in the backlog - while holding an undiagnosed condition three sections further down. Adding to a file
  without reading what is already wrong in it is how one misfiled line becomes a section.
- **Never write a machine-specific or project-specific absolute path into these documents.** They ship
  inside Conduit to every project and every customer, so `<drive>:/<your folders>/<project>` is wrong everywhere but
  the one machine it was written on, and it leaks the author's directory layout to buyers. Illustrate with
  an obviously generic example (`D:/Worlds/My Project/Temp/shot.png`) or a project-relative path
  (`Assets/Scenes/Drink System.unity`). The same goes for a real observation: keep the RULE and generalise
  the path out of it.
- **A third-party name is judged by the job it does: citation out, answer in.** The two look identical
  and the difference decides whether the line is worth anything to a reader who does not own that asset.
  - **A name used as EVIDENCE is cut** - "vendor X's shader proves the target is 5.0", "vendor Y's file
    declares this global". The reader cannot check it, the path is absent from their project, and the
    claim leaks whatever the author happened to have installed. Keep the measured VALUE and the rule
    (`#pragma target 5.0` on every renderer including the mobile variant) and generalise the source.
  - **A name that is the answer stays, in full** - "import ProTV to get an AVPro shim that restores a
    real play-mode test path". Here the name is the actionable payload; generalising it to "a third-party
    asset" leaves a reader who has the exact problem with no way to act, which is a worse document, not
    a cleaner one. **This mistake has been made in this direction too** - a sweep for proper nouns
    stripped exactly this line and had to be reverted.
  - **The author's OWN products, worlds, domains and project paths are cut in BOTH roles**, because
    naming them serves no reader and ships private work to customers.
- **A rebuttal is a citation too - write the fact, never the argument with a source the reader cannot
  see.** "Reference X says Y, and Y is wrong" is a note about your own audit. Once the source is gone -
  and a source that gets folded in always goes - the reader is left holding a correction to a claim
  nobody made in front of them. **The bar is narrow and it is not "somebody might believe this": it is
  does the API's own shape lead a reader there.**
  - **Yes - keep it, as a plain fact.** `Occupant` beside `seated`, `DeleteKey` beside `SetInt`,
    `OnVideoPause` beside `Pause()`, Unity's constraint member names on a VRC constraint: the reader's own
    reasoning produces the wrong answer, so saying it does not exist saves them the search.
  - **No - delete the clause and keep only the fact.** Nobody independently invents a rationale for why a
    blocked constructor should work. State that it is not exposed, cite the registry, stop.
  - **And never launder the source into a vague one.** "It is sometimes claimed", "a common belief",
    "most summaries list" are the same narration with the citation filed off, and they are worse: now it
    cannot even be checked. If the sentence only exists to disagree with something, it goes.
