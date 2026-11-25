using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Issue562.Tests;

/// <summary>
/// Publishes the repro app once per Fluxor version, serves each publish output from a minimal
/// Kestrel static file host on a free port, and shares one headless Chromium between tests.
/// Everything is created lazily, so a skipped test run does no work at all.
/// </summary>
public sealed class ReproAppFixture : IAsyncLifetime
{
	public const string EnableVariable = "FLUXOR_REPRO_TESTS";

	public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

	private readonly ConcurrentDictionary<string, Lazy<Task<HostedApp>>> Apps = new();
	private readonly SemaphoreSlim PublishLock = new(1, 1);
	private readonly Lazy<Task<(IPlaywright Playwright, IBrowser Browser)>> Browser;

	public ReproAppFixture()
	{
		Browser = new(async () =>
		{
			IPlaywright playwright = await Playwright.CreateAsync();
			try
			{
				IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
				return (playwright, browser);
			}
			catch (PlaywrightException ex)
			{
				playwright.Dispose();
				throw new InvalidOperationException(
					"Could not start Chromium. Install it once with: " +
					"pwsh Repro/Issue562/Issue562.Tests/bin/Release/net10.0/playwright.ps1 install chromium " +
					"(use bin/Debug/... if you built in Debug)", ex);
			}
		});
	}

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	public async ValueTask DisposeAsync()
	{
		foreach (Lazy<Task<HostedApp>> app in Apps.Values)
		{
			if (app.IsValueCreated && app.Value.IsCompletedSuccessfully)
				await app.Value.Result.Host.DisposeAsync();
		}
		if (Browser.IsValueCreated && Browser.Value.IsCompletedSuccessfully)
		{
			(IPlaywright playwright, IBrowser browser) = Browser.Value.Result;
			await browser.DisposeAsync();
			playwright.Dispose();
		}
	}

	public string BrowserVersion =>
		Browser.IsValueCreated && Browser.Value.IsCompletedSuccessfully ? Browser.Value.Result.Browser.Version : "?";

	/// <summary>Opens the app built against <paramref name="fluxorVersion"/> in a fresh browser context.</summary>
	public async Task<PageSession> OpenAsync(string fluxorVersion)
	{
		HostedApp app = await Apps.GetOrAdd(fluxorVersion, v => new Lazy<Task<HostedApp>>(() => PublishAndHostAsync(v))).Value;
		(_, IBrowser browser) = await Browser.Value;

		IBrowserContext context = await browser.NewContextAsync();
		IPage page = await context.NewPageAsync();
		var session = new PageSession(context, page);
		await page.GotoAsync(app.BaseUrl);
		return session;
	}

	private async Task<HostedApp> PublishAndHostAsync(string fluxorVersion)
	{
		string outputDirectory = Path.Combine(Path.GetTempPath(), "fluxor-issue562-repro", fluxorVersion);
		// One publish at a time: the Local variant builds the in-repo Fluxor projects
		await PublishLock.WaitAsync();
		try
		{
			if (Directory.Exists(outputDirectory))
				Directory.Delete(outputDirectory, recursive: true);
			await PublishAsync(fluxorVersion, outputDirectory);
		}
		finally
		{
			PublishLock.Release();
		}

		string wwwroot = Path.Combine(outputDirectory, "wwwroot");
		int port = GetFreePort();
		WebApplication host = CreateStaticFileHost(wwwroot, port);
		await host.StartAsync();
		return new HostedApp(host, $"http://127.0.0.1:{port}/");
	}

	private static async Task PublishAsync(string fluxorVersion, string outputDirectory)
	{
		string project = FindAppProject();
		var startInfo = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in new[] { "publish", project, "-c", "Release", $"-p:FluxorVersion={fluxorVersion}", "-o", outputDirectory, "-nologo" })
			startInfo.ArgumentList.Add(arg);

		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet");
		Task<string> stdout = process.StandardOutput.ReadToEndAsync();
		Task<string> stderr = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
			throw new InvalidOperationException(
				$"dotnet publish -p:FluxorVersion={fluxorVersion} failed ({process.ExitCode}):\n{await stdout}\n{await stderr}");
	}

	private static string FindAppProject()
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			string candidate = Path.Combine(dir.FullName, "Issue562.BlazorWasm", "Issue562.BlazorWasm.csproj");
			if (File.Exists(candidate))
				return candidate;
		}
		throw new FileNotFoundException("Could not find Issue562.BlazorWasm.csproj above " + AppContext.BaseDirectory);
	}

	private static int GetFreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	private static WebApplication CreateStaticFileHost(string wwwroot, int port)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
		{
			ContentRootPath = wwwroot,
			WebRootPath = wwwroot,
		});
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
		WebApplication app = builder.Build();

		var contentTypes = new FileExtensionContentTypeProvider();
		contentTypes.Mappings[".wasm"] = "application/wasm";
		contentTypes.Mappings[".dat"] = "application/octet-stream";
		contentTypes.Mappings[".blat"] = "application/octet-stream";
		contentTypes.Mappings[".dll"] = "application/octet-stream";
		contentTypes.Mappings[".pdb"] = "application/octet-stream";
		contentTypes.Mappings[".webcil"] = "application/octet-stream";
		contentTypes.Mappings[".json"] = "application/json";
		contentTypes.Mappings[".mjs"] = "text/javascript";
		contentTypes.Mappings[".js"] = "text/javascript";

		var files = new PhysicalFileProvider(wwwroot);
		app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
		app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = contentTypes });
		return app;
	}

	private sealed record HostedApp(WebApplication Host, string BaseUrl);
}

/// <summary>A browser page plus everything it reported to the console.</summary>
public sealed class PageSession : IAsyncDisposable
{
	private readonly IBrowserContext Context;
	private readonly ConcurrentQueue<string> Messages = new();

	public PageSession(IBrowserContext context, IPage page)
	{
		Context = context;
		Page = page;
		page.Console += (_, message) => Messages.Enqueue($"console.{message.Type}: {message.Text}");
		page.PageError += (_, error) => Messages.Enqueue($"pageerror: {error}");
	}

	public IPage Page { get; }

	/// <summary>Console messages and uncaught page errors, in the order they arrived.</summary>
	public IReadOnlyList<string> ConsoleMessages => Messages.ToArray();

	/// <summary>Called just before the browser context is closed.</summary>
	public Func<Task>? OnDisposing { get; set; }

	public async ValueTask DisposeAsync()
	{
		if (OnDisposing is not null)
			await OnDisposing();
		await Context.CloseAsync();
	}
}
