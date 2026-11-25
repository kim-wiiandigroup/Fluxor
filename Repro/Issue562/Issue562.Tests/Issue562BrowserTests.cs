using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Issue562.Tests;

/// <summary>
/// Characterization tests for Fluxor issue #562 and PR #584. Each row runs the repro app built against
/// one Fluxor version in headless Chromium and asserts what that version actually does, so the rows
/// document the bug (6.9.0), the partial fix (6.11.0) and the PR (Local = the Fluxor source in this repo).
///
/// Opt-in: set FLUXOR_REPRO_TESTS=1, otherwise every test is skipped.
/// </summary>
[Trait("Category", "BrowserRepro")]
public sealed class Issue562BrowserTests(ReproAppFixture fixture, ITestOutputHelper output) : IClassFixture<ReproAppFixture>
{
	private const string ExceptionName = "ArgumentNullException";

	/// <summary>How long we wait before concluding that something is NOT going to happen.</summary>
	private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
	private const float ExpectTimeoutMs = 5_000;

	/// <summary>
	/// Scenario A, issue #562: an effect catches the reducer's exception and dispatches StateFailureAction.
	/// </summary>
	[Theory]
	[InlineData("6.9.0")]
	[InlineData("6.11.0")]
	[InlineData("Local")]
	public async Task ScenarioA_FailureActionAfterReducerThrows(string fluxorVersion)
	{
		await using PageSession session = await OpenAsync(fluxorVersion, "A");
		IPage page = session.Page;

		await page.GetByTestId("load-bad").ClickAsync();

		if (fluxorVersion == "6.9.0")
		{
			// The bug: the Dispatcher stays "dequeuing" after the reducer throws, so StateFailureAction is
			// queued forever. The spinner never stops, no error is shown and nothing reaches the console.
			await Task.Delay(SettleTime, TestContext.Current.CancellationToken);
			Assert.Equal("true", await TextAsync(page, "is-loading"));
			Assert.Equal("", await TextAsync(page, "error-message"));
			Assert.DoesNotContain(session.ConsoleMessages, m => m.Contains(ExceptionName));
			Assert.False(await ErrorUiVisibleAsync(page));

			// Every later action is stranded too: the app is frozen
			await page.GetByTestId("increment").ClickAsync();
			await Task.Delay(SettleTime, TestContext.Current.CancellationToken);
			Assert.Equal("0", await TextAsync(page, "counter"));
			Assert.Equal("true", await TextAsync(page, "is-loading"));

			// Completely silent: not a single console error or warning, and no uncaught page error
			Assert.DoesNotContain(session.ConsoleMessages, m =>
				m.StartsWith("console.error:") || m.StartsWith("console.warning:") || m.StartsWith("pageerror:"));
			Assert.False(await ErrorUiVisibleAsync(page));
		}
		else
		{
			// 6.11.0 and Local: the failure action is processed, the error is shown and logged
			await Expect(page.GetByTestId("error-message"))
				.ToContainTextAsync("System." + ExceptionName, new() { Timeout = ExpectTimeoutMs });
			Assert.Contains("Value cannot be null", await TextAsync(page, "error-message"));
			Assert.Equal("false", await TextAsync(page, "is-loading"));
			await WaitForConsoleAsync(session, m => m.Contains("StateFailureAction") && m.Contains(ExceptionName));
			// The effect handled the exception, so Blazor reports no unhandled exception. Blazor WebAssembly
			// does show its error UI, though: it does so for anything .NET writes to stderr, and the
			// logging effect uses Console.Error. (On 6.9.0 that effect never runs, so the UI stays hidden.)
			Assert.DoesNotContain(session.ConsoleMessages, m => m.Contains("Unhandled exception"));
			await Expect(page.Locator("#blazor-error-ui")).ToBeVisibleAsync(new() { Timeout = ExpectTimeoutMs });

			await page.GetByTestId("increment").ClickAsync();
			await Expect(page.GetByTestId("counter")).ToHaveTextAsync("1", new() { Timeout = ExpectTimeoutMs });
		}
	}

	/// <summary>
	/// Scenario B, PR #584: an effect dispatches the throwing action and then a status action while the
	/// Dispatcher is still dequeuing, so the status action is queued behind the one that throws.
	/// </summary>
	[Theory]
	[InlineData("6.9.0")]
	[InlineData("6.11.0")]
	[InlineData("Local")]
	public async Task ScenarioB_ActionQueuedBehindThrowingAction(string fluxorVersion)
	{
		await using PageSession session = await OpenAsync(fluxorVersion, "B");
		IPage page = session.Page;

		await page.GetByTestId("load-and-mark").ClickAsync();

		// In every version the reducer's exception escapes the click handler's Dispatch call,
		// so Blazor logs it to the console and shows its error UI
		await WaitForConsoleAsync(session, m => m.Contains(ExceptionName));
		await Expect(page.Locator("#blazor-error-ui")).ToBeVisibleAsync(new() { Timeout = ExpectTimeoutMs });

		switch (fluxorVersion)
		{
			case "6.9.0":
				// Dispatcher stays locked: the status action and every later action are stranded
				await Task.Delay(SettleTime, TestContext.Current.CancellationToken);
				Assert.Equal("0", await TextAsync(page, "status-updates"));
				Assert.Equal("true", await TextAsync(page, "is-loading"));

				await page.GetByTestId("increment").ClickAsync();
				await Task.Delay(SettleTime, TestContext.Current.CancellationToken);
				Assert.Equal("0", await TextAsync(page, "counter"));
				Assert.Equal("0", await TextAsync(page, "status-updates"));
				break;

			case "6.11.0":
				// Dispatcher is released, but the status action stays queued...
				await Task.Delay(SettleTime, TestContext.Current.CancellationToken);
				Assert.Equal("0", await TextAsync(page, "status-updates"));
				Assert.Equal("true", await TextAsync(page, "is-loading"));

				// ...until an unrelated Dispatch drains the queue
				await page.GetByTestId("increment").ClickAsync();
				await Expect(page.GetByTestId("status-updates")).ToHaveTextAsync("1", new() { Timeout = ExpectTimeoutMs });
				Assert.Equal("1", await TextAsync(page, "counter"));
				Assert.Equal("false", await TextAsync(page, "is-loading"));
				break;

			case "Local":
				// PR #584: the queue is drained before the exception is rethrown, so the status action has
				// already been processed, without any further click
				await Expect(page.GetByTestId("status-updates")).ToHaveTextAsync("1", new() { Timeout = ExpectTimeoutMs });
				Assert.Equal("false", await TextAsync(page, "is-loading"));

				await page.GetByTestId("increment").ClickAsync();
				await Expect(page.GetByTestId("counter")).ToHaveTextAsync("1", new() { Timeout = ExpectTimeoutMs });
				Assert.Equal("1", await TextAsync(page, "status-updates"));
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(fluxorVersion), fluxorVersion, null);
		}
	}

	private async Task<PageSession> OpenAsync(string fluxorVersion, string scenario)
	{
		Assert.SkipUnless(
			ReproAppFixture.IsEnabled,
			$"Browser repro tests are opt-in: set {ReproAppFixture.EnableVariable}=1 to run them.");

		PageSession session = await fixture.OpenAsync(fluxorVersion);
		// Written when the session is disposed, so a failing row also shows what the browser saw
		session.OnDisposing = async () => await ReportAsync(session, fluxorVersion, scenario);
		IPage page = session.Page;
		// The app is loaded and Fluxor's store has processed StoreInitializedAction
		await Expect(page.GetByTestId("store-initialized")).ToHaveTextAsync("true", new() { Timeout = 60_000 });
		await Expect(page.GetByTestId("fluxor-variant")).ToHaveTextAsync(fluxorVersion);

		// Check the Fluxor assembly that actually loaded, not only the build property
		string loadedVersion = await TextAsync(page, "fluxor-version");
		if (fluxorVersion == "Local")
		{
			// The in-repo projects carry no package version, so their informational version is not 6.x
			Assert.False(
				loadedVersion.StartsWith("6.9.0") || loadedVersion.StartsWith("6.11.0"),
				$"Local build loaded a released Fluxor: {loadedVersion}");
		}
		else
		{
			Assert.StartsWith(fluxorVersion + "+", loadedVersion);
		}
		return session;
	}

	private static async Task<string> TextAsync(IPage page, string testId) =>
		(await page.GetByTestId(testId).InnerTextAsync()).Trim();

	private static Task<bool> ErrorUiVisibleAsync(IPage page) =>
		page.Locator("#blazor-error-ui").IsVisibleAsync();

	private static async Task WaitForConsoleAsync(PageSession session, Func<string, bool> predicate)
	{
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(ExpectTimeoutMs);
		while (!session.ConsoleMessages.Any(predicate))
		{
			if (DateTime.UtcNow > deadline)
				Assert.Fail("Expected console message not seen. Console:\n" + string.Join("\n", session.ConsoleMessages));
			await Task.Delay(100, TestContext.Current.CancellationToken);
		}
	}

	private async Task ReportAsync(PageSession session, string fluxorVersion, string scenario)
	{
		IPage page = session.Page;
		output.WriteLine($"Scenario {scenario}, Fluxor {fluxorVersion} ({await TextAsync(page, "fluxor-version")}), Chromium {fixture.BrowserVersion}");
		foreach (string id in new[] { "counter", "is-loading", "error-message", "status-updates" })
			output.WriteLine($"  {id} = '{await TextAsync(page, id)}'");
		output.WriteLine($"  blazor-error-ui visible = {await ErrorUiVisibleAsync(page)}");
		output.WriteLine("  Console:");
		foreach (string message in session.ConsoleMessages)
			output.WriteLine("    " + message.Replace("\n", "\n      "));
	}
}
