using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Listen on HTTP port 5000. didnt you overload that port with earth or something
// Put this behind your HTTPS reverse proxy if desired. you even left the comments in :skull:
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

const string DiscordWebhook =
    "https://discord.com/api/webhooks/1544886784599523380/vKVzzgMh_75gz8ce3E_2rU1u983Z6BKBCogefw2MP53SQANMowvmtMZuF8nx61x_SVoI";

const int MaxRequestsPerMinute = 3;
const int MaxRequestsPerHour = 20;
const int MaxStoredIps = 10000;

// Maximum request body size: 16 KB
app.Use(async (context, next) =>
{
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 16 * 1024;

    await next();
});

// In-memory IP tracking
var ipLimits = new ConcurrentDictionary<string, IpLimit>();

// Reuse one HttpClient instead of creating one per request.
var httpClient = new HttpClient();

app.MapPost("/report/", async (HttpContext context) =>
{
    string ip = GetClientIp(context);

    string source = context.Request.Form["source"].ToString().Trim();

    if (!source.Equals("mcidiots.net", StringComparison.OrdinalIgnoreCase) &&
        !source.Equals("outlandsmc.net", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Invalid source.");
    }


    // Clean up old IP entries if the table gets too large. lol timber used ai lolololol
    if (ipLimits.Count > MaxStoredIps)
    {
        foreach (var entry in ipLimits)
        {
            if (entry.Value.IsExpired())
                ipLimits.TryRemove(entry.Key, out _);
        }
    }

    var limit = ipLimits.GetOrAdd(ip, _ => new IpLimit());

    lock (limit)
    {
        if (!limit.TryRequest(out string? reason))
        {
            Console.WriteLine(
                $"[RATE LIMITED] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | IP={ip} | {reason}");

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = "60";

            return Results.Text(
                "Too many reports. Please wait before submitting another report.",
                "text/plain");
        }
    }

    IFormCollection form;

    try
    {
        form = await context.Request.ReadFormAsync();
    }
    catch
    {
        return Results.BadRequest("Invalid form data.");
    }

    string username = GetField(form, "username");
    string reported = GetField(form, "reported");
    string game = GetField(form, "game");
    string server = GetField(form, "server");
    string details = GetField(form, "details");

    // Validate required fields.
    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrWhiteSpace(reported) ||
        string.IsNullOrWhiteSpace(game) ||
        string.IsNullOrWhiteSpace(server) ||
        string.IsNullOrWhiteSpace(details))
    {
        return Results.BadRequest("All fields are required.");
    }

    // Field length limits. That's on me- and honestly? That is someting that most people wouldn't notice!
    if (username.Length > 50)
        return Results.BadRequest("Username is too long.");

    if (reported.Length > 50)
        return Results.BadRequest("Reported username is too long.");

    if (game.Length > 100)
        return Results.BadRequest("Game name is too long.");

    if (server.Length > 100)
        return Results.BadRequest("Server name is too long.");

    if (details.Length > 5000)
        return Results.BadRequest("Report details are too long.");

    // Only allow known servers.
    if (!server.Equals("OutlandsMC", StringComparison.OrdinalIgnoreCase) &&
        !server.Equals("Idiot Central", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Invalid server.");
    }

    // Basic anti-spam checks.
    if (LooksLikeSpam(username, reported, game, details))
    {
        Console.WriteLine(
            $"[SPAM BLOCKED] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | IP={ip}");

        return Results.BadRequest("Report rejected.");
    }

    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    // Build the complete report.
    var report = new StringBuilder();

    report.AppendLine("PLAYER REPORT");
    report.AppendLine("==============================");
    report.AppendLine($"Date:             {timestamp}");
    report.AppendLine($"IP:               {ip}");
    report.AppendLine();
    report.AppendLine($"In-game username: {username}");
    report.AppendLine($"Reported player:  {reported}");
    report.AppendLine($"Game:             {game}");
    report.AppendLine($"Server:           {server}");
    report.AppendLine();
    report.AppendLine("Details:");
    report.AppendLine("------------------------------");
    report.AppendLine(details);
    report.AppendLine("==============================");

    string reportText = report.ToString();

    // Console output
    Console.WriteLine();
    Console.WriteLine("========================================");
    Console.WriteLine("NEW PLAYER REPORT");
    Console.WriteLine("========================================");
    Console.WriteLine(reportText);
    Console.WriteLine("========================================");
    Console.WriteLine();

    // Discord webhook payload.
    // Discord message content has a 2000-character limit,
    // so split the report into multiple messages if necessary.
    try
    {
        foreach (string chunk in SplitForDiscord(reportText, 1900))
        {
            var payload = new
            {
                content = $"```text\n{chunk}\n```"
            };

            using var response = await httpClient.PostAsJsonAsync(
                DiscordWebhook,
                payload);

	   await httpClient.PostAsJsonAsync(
                "https://discord.com/api/webhooks/1544901257515245588/8lfv-tgWVUlQMzLA9vTaMfrHUuZQ_LfuQ7pXhlW0C-6zWYUH40hztmuN00xnMQtd809G",
                payload);

            if (!response.IsSuccessStatusCode)
            {
                string discordError = await response.Content.ReadAsStringAsync();

                Console.WriteLine(
                    $"[DISCORD ERROR] HTTP {(int)response.StatusCode}: {discordError}");

                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DISCORD ERROR] {ex.Message}");

        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }

    // Report was successfully processed.
    // Send the user to OutlandsMC with a 301 redirect.
    string redirectUrl = source.Equals("mcidiots.net", StringComparison.OrdinalIgnoreCase)
    ? "https://mcidiots.net/"
    : "https://outlandsmc.net/";

    return Results.Redirect(redirectUrl, permanent: false);
});

// Optional GET endpoint so you can quickly test that the server is alive.
app.MapGet("/report/", () =>
    Results.Text("Report API is running.", "text/plain"));

app.Run();

static string GetField(IFormCollection form, string name)
{
    return form.TryGetValue(name, out var value)
        ? value.ToString().Trim()
        : "";
}

static string GetClientIp(HttpContext context)
{
    // Deliberately don't blindly trust X-Forwarded-For.
    // If you're behind Cloudflare/nginx/etc., configure trusted
    // forwarded headers separately.
    return context.Connection.RemoteIpAddress?.ToString()
           ?? "unknown";
}

static bool LooksLikeSpam(
    string username,
    string reported,
    string game,
    string details)
{
    string combined =
        $"{username} {reported} {game} {details}".ToLowerInvariant();

    // Obvious repeated-character spam.
    if (combined.Length >= 20)
    {
        int longestRun = 1;
        int currentRun = 1;

        for (int i = 1; i < combined.Length; i++)
        {
            if (combined[i] == combined[i - 1])
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 1;
            }
        }

        if (longestRun >= 15)
            return true;
    }

    // Reject details consisting almost entirely of whitespace.
    if (details.Count(char.IsLetterOrDigit) < 5)
        return true;

    return false;
}

static IEnumerable<string> SplitForDiscord(string text, int maxLength)
{
    for (int i = 0; i < text.Length; i += maxLength)
    {
        int length = Math.Min(maxLength, text.Length - i);
        yield return text.Substring(i, length);
    }
}

class IpLimit
{
    private readonly Queue<DateTime> requests = new();

    public DateTime LastRequest { get; private set; } = DateTime.MinValue;

    public bool TryRequest(out string? reason)
    {
        DateTime now = DateTime.UtcNow;

        // Remove requests older than one hour.
        while (requests.Count > 0 &&
               requests.Peek() < now.AddHours(-1))
        {
            requests.Dequeue();
        }

        // Minimum cooldown: 20 seconds.
        if (LastRequest != DateTime.MinValue &&
            now - LastRequest < TimeSpan.FromSeconds(20))
        {
            reason = "20 second cooldown";
            return false;
        }

        // Hourly limit.
        if (requests.Count >= 20)
        {
            reason = "hourly limit";
            return false;
        }

        // Per-minute limit.
        int recentRequests = requests.Count(
            x => x >= now.AddMinutes(-1));

        if (recentRequests >= 3)
        {
            reason = "per-minute limit";
            return false;
        }

        requests.Enqueue(now);
        LastRequest = now;

        reason = null;
        return true;
    }

    public bool IsExpired()
    {
        return LastRequest != DateTime.MinValue &&
               DateTime.UtcNow - LastRequest > TimeSpan.FromHours(2);
    }
}
