using Fluxor;
using Issue562.BlazorWasm;
using Issue562.BlazorWasm.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<ICustomPropertyService, FakeCustomPropertyService>();
builder.Services.AddFluxor(options => options.ScanAssemblies(typeof(Program).Assembly));

await builder.Build().RunAsync();
