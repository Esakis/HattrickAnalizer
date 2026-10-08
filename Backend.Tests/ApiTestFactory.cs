using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "hattrick-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UseMockData"] = "true",
            ["Calibration:StoragePath"] = _storagePath,
            ["ConnectionStrings:HattrickDb"] = "",
            ["AllowedOrigins:0"] = "http://localhost:4200"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !Directory.Exists(_storagePath)) return;
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(_storagePath);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hattrick-tests"));
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(Path.GetFileName(resolved), out _))
            throw new InvalidOperationException("Refusing to remove a test storage path outside its unique temp folder.");
        Directory.Delete(resolved, recursive: true);
    }
}
