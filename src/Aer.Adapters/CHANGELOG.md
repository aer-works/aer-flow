# Changelog

## [0.20.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.19.0...adapters-v0.20.0) (2026-07-24)


### Features

* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/aer-flow/issues/435)) ([82a9d95](https://github.com/aer-works/aer-flow/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))


### Bug Fixes

* **adapters:** Enforce withheld permissions with --disallowedTools ([#380](https://github.com/aer-works/aer-flow/issues/380)) ([145d9c9](https://github.com/aer-works/aer-flow/commit/145d9c97255a551c85f812ae6870d51bfeee94c6))
* **daemon,adapters:** Stop a concurrent read failing a session metadata write ([#353](https://github.com/aer-works/aer-flow/issues/353)) ([1cb2265](https://github.com/aer-works/aer-flow/commit/1cb2265f6dc03b9fbb62869f381d362125416514))
* **daemon:** Fail closed when an interactive session has no working directory ([#402](https://github.com/aer-works/aer-flow/issues/402)) ([5a02b6f](https://github.com/aer-works/aer-flow/commit/5a02b6f4add443bf143f34a0547f676edb2cfd54))
* **daemon:** Run a directory-less session in its own dir, not the inherited cwd ([#440](https://github.com/aer-works/aer-flow/issues/440)) ([513c6d0](https://github.com/aer-works/aer-flow/commit/513c6d0ccd286f2f40f5e7dffc4a42f8415e7792))


### Code Refactoring

* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/aer-flow/issues/444)) ([04a11b8](https://github.com/aer-works/aer-flow/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/aer-flow/issues/362)) ([4b81e57](https://github.com/aer-works/aer-flow/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **daemon:** Key host session state per session so the daemon can hold more than one ([#449](https://github.com/aer-works/aer-flow/issues/449)) ([bc3bc98](https://github.com/aer-works/aer-flow/commit/bc3bc98455548e4be9c9aa7e0834aba11d1a4e8b))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.18.0...adapters-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **flow,adapters,ui:** Durably capture and surface the resolved prompt for ordinary workflow steps ([#297](https://github.com/aer-works/aer-flow/issues/297)) ([b91b3a1](https://github.com/aer-works/aer-flow/commit/b91b3a1242893df4a20cfdc3cc69044c2eea53e8))


### Bug Fixes

* **adapters,daemon:** Give chat continuation a legal Supersede target ([#291](https://github.com/aer-works/aer-flow/issues/291)) ([fb13594](https://github.com/aer-works/aer-flow/commit/fb13594513233dcd0813f504d06b6ae8ce0f474f))
* **adapters:** Grant Claude Code access to the artifacts root via --add-dir ([#299](https://github.com/aer-works/aer-flow/issues/299)) ([79c0f68](https://github.com/aer-works/aer-flow/commit/79c0f68d2495a44264514c64421500a627cb9d67))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.17.0...adapters-v0.18.0) (2026-07-21)


### Features

* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.16.0...adapters-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.15.0...adapters-v0.16.0) (2026-07-19)


### Features

* **adapters:** Add structured PermissionGrant model for worker bindings ([#230](https://github.com/aer-works/aer-flow/issues/230)) ([b958e8d](https://github.com/aer-works/aer-flow/commit/b958e8d0a1126a5f9520ab9dcb70526ac0ec87bc))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.14.0...adapters-v0.15.0) (2026-07-18)


### Features

* **adapters:** M11 Phase 1 — Canonical worker-invocation protocol + Aer.Adapters seam ([#91](https://github.com/aer-works/aer-flow/issues/91)) ([4388492](https://github.com/aer-works/aer-flow/commit/43884920d76955ff75b7d4940b2f3531f3e91315))
* **adapters:** M11 Phase 2 — Claude worker adapter (headless claude CLI) ([#92](https://github.com/aer-works/aer-flow/issues/92)) ([b7395bd](https://github.com/aer-works/aer-flow/commit/b7395bd1c33a6527217bfde32ba7e73c40f64771))
* **adapters:** M12 Phase 1 — Gemini worker adapter (headless agy CLI) ([#102](https://github.com/aer-works/aer-flow/issues/102)) ([4b944f5](https://github.com/aer-works/aer-flow/commit/4b944f5bc0ab6296ad4d408b5279d61068a80be4))
* **adapters:** M17 Phase 4 — Dispatch integration: the third adapter ([#174](https://github.com/aer-works/aer-flow/issues/174)) ([0b11c9a](https://github.com/aer-works/aer-flow/commit/0b11c9a1970fd6fb0ebd4bb6ce4f48489bd14cdb))
* **cli:** M11 Phase 3 — aer run pump (the CLI driver) ([#93](https://github.com/aer-works/aer-flow/issues/93)) ([d4648a1](https://github.com/aer-works/aer-flow/commit/d4648a1cee9e369e2a34d2d6442d4ed5eb7d2631))
* **cli:** M12 Phase 4 — Live mixed-vendor paused run (gated end-to-end) ([#106](https://github.com/aer-works/aer-flow/issues/106)) ([371049b](https://github.com/aer-works/aer-flow/commit/371049b08630fe078628693eecf1ed87732349bb))
* **core:** Initialize .NET solution and placeholder projects for aer-flow ([541258d](https://github.com/aer-works/aer-flow/commit/541258d75a43ad50fd25a63b8151d7e64c5d512c))
* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M16 Phase 4 — Worker-binding configuration editing ([#161](https://github.com/aer-works/aer-flow/issues/161)) ([b5acbb5](https://github.com/aer-works/aer-flow/commit/b5acbb583fdc658d5c1d873dc3225677c730a821))
* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))
* **setup:** initialize aer-flow repository ([801f348](https://github.com/aer-works/aer-flow/commit/801f348f5e2d1a21bbd25cd421cfd91c15b22c4d))

## Changelog
