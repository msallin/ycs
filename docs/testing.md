# Testing

What each project guards, and how to run, soak and update them. CI
(`.github/workflows/ci.yml`) runs all of it on every push.

| Project | Guards | Run |
| --- | --- | --- |
| `tests/Ycs.Tests` | Behaviour: unit tests, updates written by real Yjs (byte-identical re-encoding), 40 randomized concurrent sessions replayed from Yjs, and the public API baseline (`PublicApi.txt`). | `dotnet test tests/Ycs.Tests` |
| `tests/Ycs.Corpus` | Convergence with Yjs on a recorded corpus of Yjs 13.6.32 sessions (`interop/corpus/`): the observer's state byte for byte, and for the scripted sessions (concurrent formatting) every client's, replayed with a Ycs document per client (`ClientReplayTests`). | `dotnet test tests/Ycs.Corpus` |
| `tests/Ycs.Fuzz` | Robustness of `ApplyUpdate`/`ApplyUpdateV2` against damaged input. | `dotnet test tests/Ycs.Fuzz -c Release` |
| `tests/Ycs.Budgets` | Allocated bytes and time per operation for a fixed set of scenarios. | `dotnet test tests/Ycs.Budgets -c Release --logger "console;verbosity=detailed"` |
| `samples/Ycs.AotSample`, `samples/Ycs.WasmSample` | Native AOT and browser-wasm: publish without trim/AOT warnings and run. | see the README |
| `interop/run.sh`, `interop/corpus/roundtrip.sh` | Live round-trips against real Yjs (Node). | see the README |
| `bench/Ycs.Benchmarks` | Not a test: the editing-trace benchmark behind the README's numbers. | `bench/Ycs.Benchmarks/get-trace.sh`, then `dotnet run -c Release --project bench/Ycs.Benchmarks -- <trace.json.gz>` |

## Fuzzing (`tests/Ycs.Fuzz`)

The seeds are every update of the Yjs corpus (applied on top of the updates before it) and
updates generated in C# (`Seeds.cs`), in v1 and v2. `Mutator.cs` damages them: bit flips,
truncation, inserted, deleted, duplicated, swapped and overwritten chunks, var-uints replaced by
huge values, random bytes. For each input (`Harness.cs`):

* it finishes within 2 s (it runs on a worker thread; a hung one is abandoned);
* it allocates at most 64 MiB (for an input over 4 KiB, 16 KiB per input byte). The test process runs with a
  512 MiB GC heap limit (`runtimeconfig.template.json`), so a decoder that trusts a huge length
  fails at once with `OutOfMemoryException`, and the throw site names the allocation;
* it throws nothing but `YUpdateException` (`Harness.AllowedExceptionTypes`);
* when it throws, the document's text, state vector and v1 encoding are unchanged.

A violation is identified by its signature: the verdict, the exception type and the innermost Ycs
method it was thrown from (tiered compilation is off in this project, so inlining, and with it
the throw site, does not depend on how warm the code is).

By default the run is deterministic: 20 000 inputs from seed 1, a few seconds. For a soak:

```bash
YCS_FUZZ_ITERATIONS=1000000 YCS_FUZZ_SEED=random dotnet test tests/Ycs.Fuzz -c Release \
  --filter Name=MutatedUpdates_FailCleanly --logger "console;verbosity=detailed"
```

The output lists the verdicts and exception types seen and the seed used, so any run can be
repeated. A violation with a signature that is not in `KnownFailures.cs` fails the run and is
printed minimized, as the fields of a `PinnedCase`, ready to paste.

`KnownFailures.cs` holds the violations found and not fixed, each with its minimal input (empty
today). The fuzz run tolerates their signatures; `PinnedCases_StillFailAsPinned` replays them and is
inconclusive while one still fails, and fails once it passes: move the entry to `FixedFailures.cs`
with the fix, where `FixedCase_FailsCleanly` keeps it as a regression test.

## Public API (`tests/Ycs.Tests/PublicApi.txt`)

`PublicApi_MatchesBaseline` renders every public type of `Ycs.dll` and its public and protected
members, one line each, and compares the text with `PublicApi.txt`. When an API change is
intended, accept it and commit the file with the change:

```bash
YCS_ACCEPT_API=1 dotnet test tests/Ycs.Tests --filter PublicApi_MatchesBaseline
git diff tests/Ycs.Tests/PublicApi.txt
```

## Budgets (`tests/Ycs.Budgets`)

`Scenarios.cs` defines the scenarios (most on a 100 000-character text made of many items);
`Budgets.cs` is the one file with the numbers. Each scenario is warmed up and then averaged over
100–500 operations; the allocated bytes are counted on the test thread. A bytes budget is today's
measurement plus about 15 %; the time ceilings are about 10x today's and only catch an algorithmic
regression. The budgets are for `-c Release` (Debug is inconclusive); tiered compilation is off in
the project so that no tier-up lands in a measurement.

* A regression fails the test with the table.
* An improvement passes and the table says `improved: lower the budget to N`: put N into
  `Budgets.cs` in the same change, so the gain is kept.
* A new scenario fails with `no budget; suggest N` until its budget is added.
