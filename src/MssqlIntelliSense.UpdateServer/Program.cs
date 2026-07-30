using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var serverData = ResolveServerDataDirectory();
var releasesDirectory = Path.Combine(serverData, "releases");
var versionJsonPath = Path.Combine(serverData, "version.json");

Directory.CreateDirectory(serverData);
Directory.CreateDirectory(releasesDirectory);

app.MapGet("/", () =>
{
    var version = LoadVersionData(versionJsonPath);
    var latestVsix = FindLatestVsix(releasesDirectory, version.Version);
    var latestLink = latestVsix == null
        ? "not found"
        : "<a href=\"/releases/" + Uri.EscapeDataString(Path.GetFileName(latestVsix)) + "\">" +
          HtmlEncode(Path.GetFileName(latestVsix)) + "</a>";
    var html =
        "<!doctype html><html><head><meta charset=\"utf-8\">" +
        "<title>MSSQL IntelliSense Update Server</title>" +
        "<style>body{font-family:Segoe UI,Arial,sans-serif;margin:32px;line-height:1.45}" +
        "code{background:#f3f3f3;padding:2px 5px;border-radius:4px}</style>" +
        "</head><body><h1>MSSQL IntelliSense Update Server</h1>" +
        "<p>Status: running</p>" +
        "<p>Version: <strong>" + HtmlEncode(version.Version) + "</strong></p>" +
        "<p>Data: <code>" + HtmlEncode(serverData) + "</code></p>" +
        "<p>Version JSON: <a href=\"/version.json\">/version.json</a></p>" +
        "<p>Latest VSIX: " + latestLink + "</p>" +
        "</body></html>";
    return Results.Text(html, "text/html");
});

app.MapGet("/version.json", (HttpRequest request) =>
{
    var version = LoadVersionData(versionJsonPath);
    var latestVsix = FindLatestVsix(releasesDirectory, version.Version);
    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.Json(new
    {
        version = version.Version,
        url = baseUrl,
        changelog = version.Changelog,
        vsixUrl = latestVsix == null
            ? string.Empty
            : $"{baseUrl}/releases/{Uri.EscapeDataString(Path.GetFileName(latestVsix))}"
    });
});

app.MapMethods("/releases/{fileName}", new[] { "GET", "HEAD" }, (string fileName) =>
{
    var safeName = Path.GetFileName(fileName);
    var path = Path.Combine(releasesDirectory, safeName);
    return File.Exists(path)
        ? Results.File(path, "application/octet-stream", safeName)
        : Results.NotFound(new { error = "Release file not found.", fileName = safeName });
});

app.Run();

static string ResolveServerDataDirectory()
{
    var configured = Environment.GetEnvironmentVariable("MSSQL_INTELLISENSE_UPDATE_DATA");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current != null)
    {
        var candidate = Path.Combine(current.FullName, "server-data");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        current = current.Parent;
    }

    return Path.Combine(Environment.CurrentDirectory, "server-data");
}

static VersionData LoadVersionData(string path)
{
    if (!File.Exists(path))
    {
        return new VersionData("0.0.0", string.Empty);
    }

    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var root = document.RootElement;
    var version = root.TryGetProperty("version", out var versionProperty)
        ? versionProperty.GetString() ?? "0.0.0"
        : "0.0.0";
    var changelog = root.TryGetProperty("changelog", out var changelogProperty)
        ? changelogProperty.GetString() ?? string.Empty
        : string.Empty;
    return new VersionData(version, changelog);
}

static string? FindLatestVsix(string releasesDirectory, string version)
{
    if (!Directory.Exists(releasesDirectory))
    {
        return null;
    }

    var exact = Directory.GetFiles(releasesDirectory, $"MssqlIntelliSense.SsmsHost-{version}.vsix")
        .FirstOrDefault();
    if (exact != null)
    {
        return exact;
    }

    return Directory.GetFiles(releasesDirectory, "*.vsix")
        .Select(path => new FileInfo(path))
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .FirstOrDefault()
        ?.FullName;
}

static string HtmlEncode(string value)
{
    return System.Net.WebUtility.HtmlEncode(value);
}

internal sealed record VersionData(string Version, string Changelog);
