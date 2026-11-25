# Reproduction: issue #562 and PR #584

A small Blazor WebAssembly app, plus an automated browser test, that reproduces
[issue #562 "Fluxor endless loop for actions in dispatcher (better error handling)"](https://github.com/mrpmorris/Fluxor/issues/562)
and the follow-up gap fixed by [PR #584](https://github.com/mrpmorris/Fluxor/pull/584).

The same app can be built against Fluxor **6.9.0** (has the bug), **6.11.0** (partially fixed) or the
Fluxor source in this repository (**Local**, i.e. the PR).

## What #562 was

When a reducer throws while the `Dispatcher` is delivering an action, Fluxor 6.9.0 never clears the
dispatcher's internal "dequeuing" flag. From then on every `Dispatch` only adds the action to a queue
that nobody empties:

- the failure action your effect dispatches in its `catch` is never reduced, so the loading spinner never stops;
- nothing is written to the browser console (the exception was caught by your effect, and the code that
  would log it is triggered by the failure action that never runs);
- every button that dispatches an action stops working, until the page is reloaded.

Fluxor 6.11 releases the flag when a subscriber throws, so the failure action runs. But any actions that
were *already queued behind* the throwing action stay queued until some later, unrelated `Dispatch`.
PR #584 makes the dispatcher keep delivering the queued actions and rethrow the exception afterwards.

## The scenario

Everything is in [`Issue562/Issue562.BlazorWasm/Store`](Issue562/Issue562.BlazorWasm/Store):

- `FakeCustomPropertyService` stands in for an API. Asked for bad data, it returns (after `await Task.Delay(50)`)
  a list in which one `PropertyType` has `Name == null`.
- The reducer for `SetAllCustomPropertiesAction` indexes the list in a `Dictionary<string, PropertyType>`
  and calls `ContainsKey(p.Name)`, which throws `ArgumentNullException` for the bad entry.

**Scenario A (#562)**, button *Load custom properties (bad data)*:
`FetchCustomPropertiesAction` sets `IsLoading = true`; its effect awaits the service and dispatches
`SetAllCustomPropertiesAction` inside `try`/`catch`; the `catch` dispatches `StateFailureAction`, whose reducer
sets `IsLoading = false` and `ErrorMessage`, and whose effect logs the exception with `Console.Error.WriteLine`.

**Scenario B (#584)**, button *Load and mark finished (bad data)*:
an effect synchronously dispatches `SetAllCustomPropertiesAction` (bad data) and then
`CustomPropertiesStatusUpdatedAction` (increments `StatusUpdates`, sets `IsLoading = false`). Both are queued
behind the action that triggered the effect, so the status action sits right behind the one that throws.

The *Increment* button dispatches an unrelated `IncrementAction`, to see whether the dispatcher still works.

## Run the app manually

From `Repro/Issue562/Issue562.BlazorWasm`:

```
dotnet run -p:FluxorVersion=6.9.0
dotnet run -p:FluxorVersion=6.11.0
dotnet run                          # Local (default): the Fluxor source in this repository
```

Open the URL that is printed and keep the browser's developer console open. The page shows which Fluxor
it was built against. Each version builds into its own `obj/<version>` and `bin/<version>` folders.

## Run the automated test

The test (`Issue562/Issue562.Tests`, xunit v3 + Microsoft.Playwright) publishes the app once per version
(`dotnet publish -c Release -p:FluxorVersion=<version>` into `%TEMP%/fluxor-issue562-repro/<version>`),
serves each `wwwroot` from a minimal Kestrel static file host on a free port, and drives it with headless
Chromium in a fresh browser context, capturing console messages and page errors.

The tests are opt-in (they are skipped unless `FLUXOR_REPRO_TESTS=1`) and tagged
`[Trait("Category", "BrowserRepro")]`. They are not part of `Source/Fluxor.sln`; `Repro/Issue562.sln` has them.

```
cd Repro/Issue562/Issue562.Tests
dotnet build -c Release
pwsh bin/Release/net10.0/playwright.ps1 install chromium    # first run only
FLUXOR_REPRO_TESTS=1 dotnet test -c Release --no-build --logger "console;verbosity=detailed"
```

(PowerShell: `$env:FLUXOR_REPRO_TESTS = '1'` before `dotnet test`.) A run takes 1.5 to 5 minutes (5 on a cold build),
almost all of it the three `dotnet publish` runs. The detailed logger prints, per row, the final state on
the page, whether Blazor's error bar is visible, and every console message.

Each test row asserts what *that* version does, so the rows document the bug and the fixes rather than
all expecting the same result.

## Results

Observed on 2026-11-25 with .NET SDK 10.0.404 (runtime 10.0.15), Chromium 145.0.7632.6 (headless,
Playwright 1.58.0), Windows 11. All 6 rows passed on repeated runs.

| | Scenario A: effect catches, dispatches `StateFailureAction` | Scenario B: action queued behind the throwing one |
|---|---|---|
| **6.9.0** | Frozen. `IsLoading` stays `true`, no error message, **nothing in the console**, no error bar. *Increment* does nothing (`Counter` stays 0). | Blazor logs the unhandled `ArgumentNullException` and shows its error bar. `StatusUpdates` stays 0, `IsLoading` stays `true`, *Increment* does nothing: frozen. |
| **6.11.0** | Works. `ErrorMessage` = `System.ArgumentNullException: Value cannot be null. (Parameter 'key')`, `IsLoading` = `false`, the exception is logged. *Increment* works. | Unhandled exception logged and error bar shown. `StatusUpdates` **stays 0** and `IsLoading` stays `true`: the status action is stranded. It is only processed when *Increment* is clicked (then `StatusUpdates` = 1, `Counter` = 1). |
| **Local (PR #584)** | Same as 6.11.0. | Unhandled exception logged and error bar shown. `StatusUpdates` = 1 and `IsLoading` = `false` **right after the click**; *Increment* works. |

Notes:

- In scenario A on 6.11.0 and Local, Blazor's "An unhandled error has occurred" bar also appears, although
  no exception is unhandled: Blazor WebAssembly shows that bar for anything .NET writes to stderr, and the
  logging effect uses `Console.Error`. On 6.9.0 the logging effect never runs, so the bar never appears.
- In scenario B the exception comes out of the `Dispatch` call in the button's click handler, so Blazor
  reports it as an unhandled exception in every version. The difference is only what happens to the action
  queued behind it.
- The app sets `UseSystemResourceKeys=false` so that Release builds show real exception messages instead
  of resource keys such as `ArgumentNull_Generic`.
