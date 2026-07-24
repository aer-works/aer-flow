# Changelog

## [0.20.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.19.0...daemon-v0.20.0) (2026-07-24)


### Features

* **daemon:** Add timestamps to the task list contract ([#416](https://github.com/aer-works/aer-flow/issues/416)) ([439c927](https://github.com/aer-works/aer-flow/commit/439c927a31bf338bb2d8b194adc230f0a54c9d94))


### Bug Fixes

* **daemon,adapters:** Stop a concurrent read failing a session metadata write ([#353](https://github.com/aer-works/aer-flow/issues/353)) ([1cb2265](https://github.com/aer-works/aer-flow/commit/1cb2265f6dc03b9fbb62869f381d362125416514))
* **daemon,ui:** broadcast desktop-started runs to connected WS clients ([#401](https://github.com/aer-works/aer-flow/issues/401)) ([ef9f0c5](https://github.com/aer-works/aer-flow/commit/ef9f0c5b49b19910f1a768f96d9e204073977389))
* **daemon:** Fail closed when an interactive session has no working directory ([#402](https://github.com/aer-works/aer-flow/issues/402)) ([5a02b6f](https://github.com/aer-works/aer-flow/commit/5a02b6f4add443bf143f34a0547f676edb2cfd54))
* **daemon:** Guard session re-materialization against live flow state ([#394](https://github.com/aer-works/aer-flow/issues/394)) ([951f69a](https://github.com/aer-works/aer-flow/commit/951f69a90e8d587271d8f437f53425e0e40f1fe0))
* **daemon:** Keep --remote port stable so a restart doesn't strand paired phones ([#400](https://github.com/aer-works/aer-flow/issues/400)) ([25c8631](https://github.com/aer-works/aer-flow/commit/25c8631fbfdad0c8712c3734472b6c7acf3c3f71))
* **daemon:** Return a meaningful message when opening a locked task ([#415](https://github.com/aer-works/aer-flow/issues/415)) ([f33cf89](https://github.com/aer-works/aer-flow/commit/f33cf89b5edbf954ada40a5a81aeafda721dfbc8))
* **daemon:** Run a directory-less session in its own dir, not the inherited cwd ([#440](https://github.com/aer-works/aer-flow/issues/440)) ([513c6d0](https://github.com/aer-works/aer-flow/commit/513c6d0ccd286f2f40f5e7dffc4a42f8415e7792))
* **daemon:** Serialize per-session turns so re-materialization can't race a live turn ([#441](https://github.com/aer-works/aer-flow/issues/441)) ([318c85d](https://github.com/aer-works/aer-flow/commit/318c85df01dbf409e4601ce90e600a2e697fe5ef))


### Code Refactoring

* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/aer-flow/issues/444)) ([04a11b8](https://github.com/aer-works/aer-flow/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/aer-flow/issues/362)) ([4b81e57](https://github.com/aer-works/aer-flow/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **daemon:** Extract the broadcast subsystem into DaemonBroadcast ([#433](https://github.com/aer-works/aer-flow/issues/433)) ([03245fa](https://github.com/aer-works/aer-flow/commit/03245facf740ce439d27bcbac18034fc65a41a19))
* **daemon:** Key host session state per session so the daemon can hold more than one ([#449](https://github.com/aer-works/aer-flow/issues/449)) ([bc3bc98](https://github.com/aer-works/aer-flow/commit/bc3bc98455548e4be9c9aa7e0834aba11d1a4e8b))


### Documentation

* Retire IMPLEMENTATION_PLAN.md into gated homes and audit the doc surface ([#379](https://github.com/aer-works/aer-flow/issues/379)) ([5a0b2ba](https://github.com/aer-works/aer-flow/commit/5a0b2ba04854245beca447ad1b1611b02b96a461))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.18.0...daemon-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))


### Bug Fixes

* **adapters,daemon:** Give chat continuation a legal Supersede target ([#291](https://github.com/aer-works/aer-flow/issues/291)) ([fb13594](https://github.com/aer-works/aer-flow/commit/fb13594513233dcd0813f504d06b6ae8ce0f474f))
* **daemon,ui,mobile:** Wire command picker to real actions and show active mode ([#298](https://github.com/aer-works/aer-flow/issues/298)) ([3ed5d2d](https://github.com/aer-works/aer-flow/commit/3ed5d2d0b4841bb84c5d43c6e7b58bc3ef2398d8))


### Tests

* **daemon,ui:** Use OS-assigned dynamic ports for test-fixture daemon instances ([#302](https://github.com/aer-works/aer-flow/issues/302)) ([f41bf43](https://github.com/aer-works/aer-flow/commit/f41bf43dd0f367de40832646d3c2070f68cfc99f))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.17.0...daemon-v0.18.0) (2026-07-21)


### Miscellaneous

* **daemon:** Synchronize desktop versions

## [0.17.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.16.0...daemon-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.15.0...daemon-v0.16.0) (2026-07-19)


### Features

* **daemon,ui,sidecar:** M21 Phase 5+6 — Zero-Config Tailscale Embedding + Close M20's Deferred Hardening ([#244](https://github.com/aer-works/aer-flow/issues/244)) ([90fb9f9](https://github.com/aer-works/aer-flow/commit/90fb9f9d01145befa3255b62823f8adb2bfc13dd))
* **mobile:** Aer.Mobile Flutter client for remote decision-inbox control ([#233](https://github.com/aer-works/aer-flow/issues/233)) ([42d6a7e](https://github.com/aer-works/aer-flow/commit/42d6a7e0caedd4d1fc95d7d2564798c0b5057e9e))
* **ui,mobile:** M21 Phase 3 — Desktop Pairing UX ([#235](https://github.com/aer-works/aer-flow/issues/235)) ([946743c](https://github.com/aer-works/aer-flow/commit/946743cd88176e05075f8c9264885e9ca830b57f))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.14.0...daemon-v0.15.0) (2026-07-18)


### Features

* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))

## Changelog
