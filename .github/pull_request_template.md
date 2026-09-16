<!--
Thanks for contributing. Delete any section that does not apply — this is a prompt, not a form.

RECORDING A CASSETTE? The four bullets under "If this is a cassette recording" are the ones #283
asks for; they are here so you do not have to go back and find them.
-->

## What this changes

<!-- One or two sentences. What was wrong or missing, and what it does now. -->

## What you verified

<!--
Which tests you ran, given the tiers in CONTRIBUTING.md. "Ran the fast tier on Linux" is a fine
answer. So is "I could not run the Docker tier" — an unstated gap is worse than a stated one.
-->

## What stays unverified

<!--
Optional but valued. Every guarantee has an edge; naming yours saves the next person from assuming
it does not.
-->

---

### If this is a performance change

- [ ] One variable changed per measurement
- [ ] Build kept outside the timing
- [ ] Median of several runs quoted, with the spread

<!--
Bundling two changes into one measurement credits the wrong one. That happened on #640, where a
pragma took credit for a gain it did not produce and the conclusion inverted once the two were
separated.
-->

### If this is a cassette recording

- [ ] **Which service and which endpoints** the recording covers
- [ ] **The date**, and the **API version** if visible — a recording is evidence of one exchange on one day
- [ ] **What you deliberately did not cover** — e.g. "read only, never touched the write path"
- [ ] **Why you picked that account, project or repo**, when the choice affects the cassette's size
- [ ] `CassetteSecretTests` green, and the response body checked for personal data — including fields the connector never reads

<!--
A recording that reveals the connector is BROKEN is the most valuable outcome. Open an issue for it;
do not edit the cassette to match the code.
-->
