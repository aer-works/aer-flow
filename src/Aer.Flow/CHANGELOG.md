# Changelog

## [0.16.0](https://github.com/aer-works/aer-flow/compare/flow-v0.15.0...flow-v0.16.0) (2026-07-19)


### Miscellaneous

* **flow:** Synchronize core versions

## [0.15.0](https://github.com/aer-works/aer-flow/compare/flow-v0.14.0...flow-v0.15.0) (2026-07-18)


### Features

* **adapters:** M12 Phase 1 — Gemini worker adapter (headless agy CLI) ([#102](https://github.com/aer-works/aer-flow/issues/102)) ([4b944f5](https://github.com/aer-works/aer-flow/commit/4b944f5bc0ab6296ad4d408b5279d61068a80be4))
* **cli:** M11 Phase 3 — aer run pump (the CLI driver) ([#93](https://github.com/aer-works/aer-flow/issues/93)) ([d4648a1](https://github.com/aer-works/aer-flow/commit/d4648a1cee9e369e2a34d2d6442d4ed5eb7d2631))
* **cli:** M12 Phase 2 — aer cancel + Ctrl+C host-stop wiring ([#103](https://github.com/aer-works/aer-flow/issues/103)) ([08b8d7a](https://github.com/aer-works/aer-flow/commit/08b8d7abd9da77c404ded2a5ce0c764407afca05))
* **core:** Initialize .NET solution and placeholder projects for aer-flow ([541258d](https://github.com/aer-works/aer-flow/commit/541258d75a43ad50fd25a63b8151d7e64c5d512c))
* **flow:** Add the Dependency Resolver for step readiness ([#38](https://github.com/aer-works/aer-flow/issues/38)) ([c460641](https://github.com/aer-works/aer-flow/commit/c4606410d54b3988c9139e2d327e31578948bc1f))
* **flow:** Add the Log Manager for crash-safe flow.jsonl appends ([#35](https://github.com/aer-works/aer-flow/issues/35)) ([8d8a728](https://github.com/aer-works/aer-flow/commit/8d8a7281653b87825a06fa4600b7edc146f22ddb))
* **flow:** Add the State Projector for FlowState reconstruction ([#37](https://github.com/aer-works/aer-flow/issues/37)) ([6601423](https://github.com/aer-works/aer-flow/commit/66014230b0aec75181105dfd4ecbdee6a4341b7a))
* **flow:** Add the Template Parser and Snapshot Binder ([#36](https://github.com/aer-works/aer-flow/issues/36)) ([75090a1](https://github.com/aer-works/aer-flow/commit/75090a1925023150fd5c80f8a776610f8094b30f))
* **flow:** Define the Phase 1 domain model ([#34](https://github.com/aer-works/aer-flow/issues/34)) ([61db539](https://github.com/aer-works/aer-flow/commit/61db539efef509d46167d08d0365b09989d70b46))
* **flow:** M10 Phase 1 — Cancellation mutation surface: record, validate, non-process targets ([#75](https://github.com/aer-works/aer-flow/issues/75)) ([df31f67](https://github.com/aer-works/aer-flow/commit/df31f671fa8a1a9259fdc42d5738a6f3f5dac5c0))
* **flow:** M10 Phase 2 — Live cancellation delivery: in-flight Core executions ([#76](https://github.com/aer-works/aer-flow/issues/76)) ([196410e](https://github.com/aer-works/aer-flow/commit/196410ece90a02c0bdb82ea12520dfa26985cf61))
* **flow:** M10 Phase 3 — Crash-recovery reconciliation: reading back the Core log ([#80](https://github.com/aer-works/aer-flow/issues/80)) ([8332030](https://github.com/aer-works/aer-flow/commit/833203098219d7a6c8f8f2ae65d04cbb6e2cec05))
* **flow:** M10 Phase 4 — Cancellation + crash-recovery end-to-end integration tests ([#82](https://github.com/aer-works/aer-flow/issues/82)) ([37afd3d](https://github.com/aer-works/aer-flow/commit/37afd3de76e0c51c21f05d4d883b67884d19a0d8))
* **flow:** M7 Phase 6 — Artifact Manager + Core Dispatcher ([#41](https://github.com/aer-works/aer-flow/issues/41)) ([1a633ce](https://github.com/aer-works/aer-flow/commit/1a633ce4f0794f52730122c2d6f573958ad4ba1d))
* **flow:** M7 Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface ([#43](https://github.com/aer-works/aer-flow/issues/43)) ([97b90a7](https://github.com/aer-works/aer-flow/commit/97b90a79cfebea010a73e6c3eb8efaf37de20350))
* **flow:** M7 Phase 8 — Concurrency Guard + end-to-end integration test ([#44](https://github.com/aer-works/aer-flow/issues/44)) ([eea819f](https://github.com/aer-works/aer-flow/commit/eea819f3a27724ee6af182215ea0aa8ccae950b8))
* **flow:** M8 Phase 1 — Attempt-history projection ([#51](https://github.com/aer-works/aer-flow/issues/51)) ([5579774](https://github.com/aer-works/aer-flow/commit/55797741cc8a48490bf8f4ae7b29fef1ea8fb452))
* **flow:** M8 Phase 2 — Retry Engine + retry-aware readiness ([#53](https://github.com/aer-works/aer-flow/issues/53)) ([a45ad6d](https://github.com/aer-works/aer-flow/commit/a45ad6d745ca029e20f4d50368ca4ba568f8d375))
* **flow:** M8 Phase 3 — Reactive concurrent dispatch ([#54](https://github.com/aer-works/aer-flow/issues/54)) ([afccaf5](https://github.com/aer-works/aer-flow/commit/afccaf5510b085e188023b7037ed809abe3d5806))
* **flow:** M9 Phase 1 — Pause Engine ([#64](https://github.com/aer-works/aer-flow/issues/64)) ([6aa4ff0](https://github.com/aer-works/aer-flow/commit/6aa4ff0330db7a6f727fc565b5fb512a14212220))
* **flow:** M9 Phase 2 — External Decision Handler: record, validate, Resume/Reject ([#65](https://github.com/aer-works/aer-flow/issues/65)) ([a8e580f](https://github.com/aer-works/aer-flow/commit/a8e580f6a30ca0c5ce09a1d8097a75fa618a0704))
* **flow:** M9 Phase 3 — RetryWithRevision + Supersede + the invalidation cascade ([#66](https://github.com/aer-works/aer-flow/issues/66)) ([3569690](https://github.com/aer-works/aer-flow/commit/35696900a6732bb5033680d495edba6f877f02b7))
* **flow:** M9 Phase 4 — Human worker support (non-process executions) ([#67](https://github.com/aer-works/aer-flow/issues/67)) ([e24f273](https://github.com/aer-works/aer-flow/commit/e24f2738cef966d21e8895e82a29761b7c64dd6e))
* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M16 Phase 1 — Template write seam + create/save walking skeleton ([#158](https://github.com/aer-works/aer-flow/issues/158)) ([e925a57](https://github.com/aer-works/aer-flow/commit/e925a57c727cfb4d6c6c2e61d24dba46af09c653))
* **ui:** M16 Phase 3 — PausePoint + SupersedeTargets editing ([#162](https://github.com/aer-works/aer-flow/issues/162)) ([46f66cd](https://github.com/aer-works/aer-flow/commit/46f66cd4628de3d024d9b344d797d4737bcd4369))


### Bug Fixes

* **flow:** Order in-flight registry capture before the round's log read ([#83](https://github.com/aer-works/aer-flow/issues/83)) ([55dfbd9](https://github.com/aer-works/aer-flow/commit/55dfbd99ceb605ded552b6a5117ca140bb296ae5))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))
* **setup:** initialize aer-flow repository ([801f348](https://github.com/aer-works/aer-flow/commit/801f348f5e2d1a21bbd25cd421cfd91c15b22c4d))

## Changelog
