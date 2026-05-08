// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using Spectre.Console;

// ── Prompts ────────────────────────────────────────────────────────────────
string rootFolder = AnsiConsole.Ask<string>(
    "[yellow]Root folder[/] containing cloned repos:");

while (!Directory.Exists(rootFolder))
{
    AnsiConsole.MarkupLine("[red]Folder not found.[/] Try again.");
    rootFolder = AnsiConsole.Ask<string>("[yellow]Root folder[/]:");
}

int yearA = AnsiConsole.Ask<int>("Compare [green]Year A[/] (older):", 2024);
int yearB = AnsiConsole.Ask<int>("    vs. [green]Year B[/] (newer):", 2025);

int startMonth = AnsiConsole.Ask<int>("Start month [grey](1-12)[/]:", 1);
int endMonth   = AnsiConsole.Ask<int>("End month   [grey](1-12)[/]:", 3);

string sinceA = $"{yearA}-{startMonth:D2}-01";
string untilA = $"{yearA}-{endMonth:D2}-{DateTime.DaysInMonth(yearA, endMonth):D2}";
string sinceB = $"{yearB}-{startMonth:D2}-01";
string untilB = $"{yearB}-{endMonth:D2}-{DateTime.DaysInMonth(yearB, endMonth):D2}";

// ── Discover repos ─────────────────────────────────────────────────────────
var repoDirs = Directory.GetDirectories(rootFolder, ".git", SearchOption.AllDirectories)
    .Select(g => Directory.GetParent(g)!.FullName)
    .OrderBy(p => p)
    .ToList();

if (repoDirs.Count == 0)
{
    AnsiConsole.MarkupLine("[red]No git repositories found under that folder.[/]");
    return;
}

AnsiConsole.MarkupLine($"\nFound [cyan]{repoDirs.Count}[/] repositories.");

// ── Optional: pull latest ──────────────────────────────────────────────────
bool doPull = AnsiConsole.Confirm("Pull latest for all repos before counting?", defaultValue: true);

var pullFailures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

if (doPull)
{
    AnsiConsole.MarkupLine("");
    await AnsiConsole.Progress()
        .AutoRefresh(true)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var pullTask = ctx.AddTask("[blue]Pulling repos[/]", maxValue: repoDirs.Count);

            // Sequential: git pull can prompt for credentials or lock the index
            foreach (var repoPath in repoDirs)
            {
                string name = Path.GetFileName(repoPath);
                pullTask.Description = $"[blue]Pulling[/] {Markup.Escape(name)}";

                // Detect whether the repo uses "main" or "master"
                string? defaultBranch = null;
                foreach (var candidate in new[] { "main", "master" })
                {
                    var (rc, _) = await RunGitAsync(repoPath,
                        $"rev-parse --verify refs/heads/{candidate}");
                    if (rc == 0) { defaultBranch = candidate; break; }
                }

                if (defaultBranch is null)
                {
                    pullFailures[name] = "Could not find a 'main' or 'master' branch.";
                    pullTask.Increment(1);
                    continue;
                }

                // Checkout the default branch before pulling
                var (checkoutCode, checkoutErr) = await RunGitAsync(repoPath,
                    $"checkout {defaultBranch}");
                if (checkoutCode != 0)
                {
                    pullFailures[name] = $"checkout {defaultBranch}: {checkoutErr.Trim()}";
                    pullTask.Increment(1);
                    continue;
                }

                var (exitCode, stderr) = await RunGitAsync(repoPath, "pull --ff-only");
                if (exitCode != 0)
                    pullFailures[name] = stderr.Trim();

                pullTask.Increment(1);
            }

            pullTask.Description = "[blue]Pull complete[/]";
        });

    if (pullFailures.Count > 0)
        AnsiConsole.MarkupLine($"[yellow]⚠ {pullFailures.Count} repo(s) could not be pulled (shown in table).[/]");
}

AnsiConsole.MarkupLine("");

// ── Gather stats ───────────────────────────────────────────────────────────
var results = new System.Collections.Concurrent.ConcurrentBag<RepoStats>();

await AnsiConsole.Progress()
    .AutoRefresh(true)
    .Columns(
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new SpinnerColumn())
    .StartAsync(async ctx =>
    {
        var task = ctx.AddTask("[green]Processing repos[/]", maxValue: repoDirs.Count);

        await Parallel.ForEachAsync(
            repoDirs,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (repoPath, ct) =>
            {
                string name = Path.GetFileName(repoPath);
                var (addA, delA) = await GetStats(repoPath, sinceA, untilA);
                var (addB, delB) = await GetStats(repoPath, sinceB, untilB);

                results.Add(new RepoStats(name, addA, delA, addB, delB));
                task.Increment(1);
            });
    });

// ── Render table ───────────────────────────────────────────────────────────
var sorted = results.OrderBy(r => r.Name).ToList();

var table = new Table()
    .Border(TableBorder.Rounded)
    .AddColumn(new TableColumn("[white]Repository[/]"))
    .AddColumn(new TableColumn($"[green]{yearA} Added[/]").RightAligned())
    .AddColumn(new TableColumn($"[red]{yearA} Deleted[/]").RightAligned())
    .AddColumn(new TableColumn($"[green]{yearB} Added[/]").RightAligned())
    .AddColumn(new TableColumn($"[red]{yearB} Deleted[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Δ Added[/]").RightAligned())
    .AddColumn(new TableColumn("[cyan]Δ Deleted[/]").RightAligned());

long totalAddA = 0, totalDelA = 0, totalAddB = 0, totalDelB = 0;

foreach (var r in sorted)
{
    totalAddA += r.AddedA; totalDelA += r.DeletedA;
    totalAddB += r.AddedB; totalDelB += r.DeletedB;

    long deltaAdd = r.AddedB  - r.AddedA;
    long deltaDel = r.DeletedB - r.DeletedA;

    bool hasPullError = pullFailures.ContainsKey(r.Name);
    string nameCell = hasPullError
        ? $"[yellow]⚠ {Markup.Escape(r.Name)}[/]"
        : Markup.Escape(r.Name);

    table.AddRow(
        nameCell,
        $"[green]{r.AddedA:N0}[/]",
        $"[red]{r.DeletedA:N0}[/]",
        $"[green]{r.AddedB:N0}[/]",
        $"[red]{r.DeletedB:N0}[/]",
        DeltaMarkup(deltaAdd),
        DeltaMarkup(deltaDel));
}

// Totals row
table.AddEmptyRow();
table.AddRow(
    "[bold]TOTAL[/]",
    $"[bold green]{totalAddA:N0}[/]",
    $"[bold red]{totalDelA:N0}[/]",
    $"[bold green]{totalAddB:N0}[/]",
    $"[bold red]{totalDelB:N0}[/]",
    $"[bold]{DeltaMarkup(totalAddB - totalAddA)}[/]",
    $"[bold]{DeltaMarkup(totalDelB - totalDelA)}[/]");

AnsiConsole.Write(table);
AnsiConsole.MarkupLine($"\n[grey]Date range: {startMonth:D2}/01 – end of month {endMonth:D2}[/]");

// ── Helpers ────────────────────────────────────────────────────────────────
static async Task<(long added, long deleted)> GetStats(string repoPath, string since, string until)
{
    var (_, output) = await RunGitAsync(repoPath,
        $"log --since={since} --until={until} --pretty=tformat: --numstat",
        captureStdout: true);

    long added = 0, deleted = 0;

    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        // numstat format: <added>\t<deleted>\t<filename>
        // Binary files show "-" instead of numbers
        var parts = line.Split('\t');
        if (parts.Length >= 2
            && long.TryParse(parts[0], out long a)
            && long.TryParse(parts[1], out long d))
        {
            added   += a;
            deleted += d;
        }
    }

    return (added, deleted);
}

static async Task<(int exitCode, string output)> RunGitAsync(
    string repoPath, string arguments, bool captureStdout = false)
{
    var psi = new ProcessStartInfo("git")
    {
        Arguments = arguments,
        WorkingDirectory = repoPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)!;
    string stdout = await process.StandardOutput.ReadToEndAsync();
    string stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return (process.ExitCode, captureStdout ? stdout : stderr);
}

static string DeltaMarkup(long delta) =>
    delta switch
    {
        > 0 => $"[green]+{delta:N0}[/]",
        < 0 => $"[red]{delta:N0}[/]",
        _   => "[grey]0[/]"
    };

// ── Record ─────────────────────────────────────────────────────────────────
record RepoStats(string Name, long AddedA, long DeletedA, long AddedB, long DeletedB);
