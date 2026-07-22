# Changelog

## [0.19.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.18.0...ui-core-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **flow,adapters,ui:** Durably capture and surface the resolved prompt for ordinary workflow steps ([#297](https://github.com/aer-works/aer-flow/issues/297)) ([b91b3a1](https://github.com/aer-works/aer-flow/commit/b91b3a1242893df4a20cfdc3cc69044c2eea53e8))
* **ui,mobile:** Add a direct "start new chat" entry point ([#301](https://github.com/aer-works/aer-flow/issues/301)) ([03c2c62](https://github.com/aer-works/aer-flow/commit/03c2c62b8178878664083f015877a1d68637d593))
* **ui,mobile:** Bulk select for archive/delete in the Tasks view ([#294](https://github.com/aer-works/aer-flow/issues/294)) ([263bc4d](https://github.com/aer-works/aer-flow/commit/263bc4d3917a2a1a50e8b1571c8b642c54c6faba))


### Bug Fixes

* **daemon,ui,mobile:** Wire command picker to real actions and show active mode ([#298](https://github.com/aer-works/aer-flow/issues/298)) ([3ed5d2d](https://github.com/aer-works/aer-flow/commit/3ed5d2d0b4841bb84c5d43c6e7b58bc3ef2398d8))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.17.0...ui-core-v0.18.0) (2026-07-21)


### Features

* **dialogue:** M23 Phase 1 — generalize the dialogue worker to N-party ([#273](https://github.com/aer-works/aer-flow/issues/273)) ([0a44f58](https://github.com/aer-works/aer-flow/commit/0a44f58062f9eda622452852e0e1ed29217b75b1))
* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.16.0...ui-core-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.15.0...ui-core-v0.16.0) (2026-07-19)


### Features

* **adapters:** Add structured PermissionGrant model for worker bindings ([#230](https://github.com/aer-works/aer-flow/issues/230)) ([b958e8d](https://github.com/aer-works/aer-flow/commit/b958e8d0a1126a5f9520ab9dcb70526ac0ec87bc))
* **daemon,ui,sidecar:** M21 Phase 5+6 — Zero-Config Tailscale Embedding + Close M20's Deferred Hardening ([#244](https://github.com/aer-works/aer-flow/issues/244)) ([90fb9f9](https://github.com/aer-works/aer-flow/commit/90fb9f9d01145befa3255b62823f8adb2bfc13dd))
* **ui,mobile:** M21 Phase 3 — Desktop Pairing UX ([#235](https://github.com/aer-works/aer-flow/issues/235)) ([946743c](https://github.com/aer-works/aer-flow/commit/946743cd88176e05075f8c9264885e9ca830b57f))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.14.0...ui-core-v0.15.0) (2026-07-18)


### Features

* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M19 Phase 2 - Navigation shell, Aer.Ui.Core seam, MVVM re-home, and the decision inbox ([#195](https://github.com/aer-works/aer-flow/issues/195)) ([2f25168](https://github.com/aer-works/aer-flow/commit/2f25168d843c609d986a0a73679e93087577c831))
* **ui:** M19 Phase 3 - Task view, human-first ([#196](https://github.com/aer-works/aer-flow/issues/196)) ([2743338](https://github.com/aer-works/aer-flow/commit/2743338e07313087221d6c091dceea3fe9592d0f))
* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))
* **ui:** M19 Phase 5 - Visual design pass ([#198](https://github.com/aer-works/aer-flow/issues/198)) ([8b500f5](https://github.com/aer-works/aer-flow/commit/8b500f56d6e0eb2e7d5175f2d58bfe8d675a1c9a))


### Bug Fixes

* **ui:** Polish title bar, navigation rail transitions, and step preview cache ([#221](https://github.com/aer-works/aer-flow/issues/221)) ([fc0ae3c](https://github.com/aer-works/aer-flow/commit/fc0ae3c17a66542a3a18dbf11073e11c2990bfc7))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))

## Changelog
